using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Apps;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.Spark.Tests.IdentityProvider;
using MintPlayer.Spark.Tests.Organizations;
using MintPlayer.Webhooks;

namespace MintPlayer.Spark.Tests.Apps;

/// <summary>
/// #691 (<c>docs/apps_webhooks_PRD.md</c> D26): who may call <c>/spark/apps</c>. The group needs a signed-in caller; each
/// endpoint then needs the caller's id (the NameIdentifier claim), so a principal without one, such as a scheme that
/// only names its caller, gets 401 from every endpoint. The catalog is public, and lists the extra signers' public keys.
/// </summary>
public class AppsCallerTests : AppsTestHost
{
    private const string NoIdHeader = "X-No-Id";

    protected override void ConfigureAppsBuilder(AppsBuilder apps) => apps.EnableApps().AddSigner(new FakeSigner(), "whpk_test");

    protected override void ConfigureSpark(ISparkBuilder spark)
    {
        spark.AddIdentityProvider(o =>
        {
            o.Issuer = "https://idp.example.com";
            o.SigningKeyPath = OidcTestHost.CopyOfSharedSigningKey();
        });
        spark.Services.AddTransient<IStartupFilter, NoIdPrincipalStartupFilter>();
    }

    private sealed class FakeSigner : IWebhookSigner
    {
        public string Version => "v2a";
        public byte[] Sign(WebhookSigningInput input) => new byte[64];
    }

    /// <summary>A signed-in principal with a name but no NameIdentifier claim.</summary>
    private sealed class NoIdPrincipalStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(NoIdHeader, out var name) && name.ToString() is { Length: > 0 } value)
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, value)], "NoIdTest"));
                await nextMiddleware();
            });
            next(app);
        };
    }

    private static readonly string[] Gets =
    [
        "/spark/apps/webhooks/owners",
        "/spark/apps/webhooks/endpoints?ownerKind=user&ownerKey=x",
        "/spark/apps/webhooks/deliveries?endpointId=ep_000000000000000000000000",
        "/spark/apps/apps?ownerKind=user&ownerKey=x",
        "/spark/apps/apps/app?slug=bot",
        "/spark/apps/installations/new?slug=bot",
        "/spark/apps/installations?targetKind=user&targetKey=x",
        "/spark/apps/apps/installations?slug=bot",
    ];

    private static readonly string[] Posts =
    [
        "/spark/apps/webhooks/endpoints",
        "/spark/apps/webhooks/endpoints/update",
        "/spark/apps/webhooks/endpoints/delete",
        "/spark/apps/webhooks/endpoints/rotate-secret",
        "/spark/apps/webhooks/endpoints/enable",
        "/spark/apps/webhooks/endpoints/ping",
        "/spark/apps/webhooks/deliveries/redeliver",
        "/spark/apps/apps",
        "/spark/apps/apps/update",
        "/spark/apps/apps/delete",
        "/spark/apps/apps/keys/generate",
        "/spark/apps/apps/keys/delete",
        "/spark/apps/installations",
        "/spark/apps/installations/suspend",
        "/spark/apps/installations/unsuspend",
        "/spark/apps/installations/accept",
        "/spark/apps/installations/delete",
    ];

    [Fact]
    public async Task A_caller_without_an_id_gets_401_from_every_endpoint()
    {
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add(NoIdHeader, "robot");
        // Minted as the same principal: the antiforgery token is bound to it.
        var (cookie, xsrf) = await OrganizationsTestHost.MintAsync(client, null);

        foreach (var url in Gets)
            (await client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"GET {url} needs the caller's id");
        foreach (var url in Posts)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { }) };
            request.Headers.Add("Cookie", cookie);
            request.Headers.Add("X-XSRF-TOKEN", xsrf);
            (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"POST {url} needs the caller's id");
        }
    }

    [Fact]
    public async Task Anyone_reads_the_catalog_with_the_extra_signers_public_keys()
    {
        var (status, catalog) = await GetAsync("/spark/apps/webhooks/catalog", userId: null);

        status.Should().Be(HttpStatusCode.OK, "the catalog is public, for codegen");
        catalog.GetProperty("x-spark-signing-keys").GetProperty("v2a").GetString().Should().Be("whpk_test");
        catalog.GetProperty("x-spark-permissions").EnumerateArray().Select(p => p.GetString()).Should().Contain("orders:read");
        (await GetAsync("/spark/apps/webhooks/owners", userId: null)).Status.Should().Be(HttpStatusCode.Unauthorized, "everything else needs a signed-in caller");
    }
}
