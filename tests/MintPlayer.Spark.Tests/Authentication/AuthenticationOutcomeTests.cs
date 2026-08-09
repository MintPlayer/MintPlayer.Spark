using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Tests.Authentication;

/// <summary>
/// What a request actually looks like downstream in each authentication outcome.
/// <para>
/// These pin the claims made in <c>docs/guide-authentication-schemes.md</c>. The one that matters
/// is the equivalence: a credential that every scheme <b>refused</b> arrives at the endpoint
/// indistinguishable from no credential at all. That is a deliberate property of ASP.NET's
/// authentication middleware — it assigns <c>HttpContext.User</c> only when a result carries a
/// principal, and both <c>Fail</c> and <c>NoResult</c> carry none — but it is the kind of framework
/// behaviour that gets asserted from memory and turns out to be wrong, so it is asserted here
/// against a running pipeline instead.
/// </para>
/// <para>
/// The consequence is documented rather than fixed: a rejected credential yields the same rights
/// as anonymity (the <c>Everyone</c> baseline), and the only trace it leaves is the composite
/// handler's warning.
/// </para>
/// </summary>
public class AuthenticationOutcomeTests
{
    private const string ProbeScheme = "Probe";
    private const string Header = "X-Probe-Credential";

    private sealed class ProbeHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(Header, out var presented))
                return Task.FromResult(AuthenticateResult.NoResult());

            if (presented != "valid")
                return Task.FromResult(AuthenticateResult.Fail("Refused by the probe scheme."));

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "probe-user")], ProbeScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), ProbeScheme)));
        }
    }

    /// <summary>
    /// A host carrying only the composite scheme and one probe credential — no Spark, no RavenDB.
    /// The subject here is the authentication pipeline itself, so anything else would be noise.
    /// </summary>
    private static async Task<IHost> StartHostAsync()
    {
        var registry = new SparkModuleRegistry();
        registry.AddCredentialScheme(ProbeScheme);

        return await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(registry);
                    services.AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, ProbeHandler>(ProbeScheme, _ => { })
                        .AddScheme<AuthenticationSchemeOptions, SparkCompositeAuthenticationHandler>(
                            SparkAuthenticationDefaults.CompositeScheme, _ => { });
                    services.PostConfigure<AuthenticationOptions>(o =>
                        o.DefaultAuthenticateScheme = SparkAuthenticationDefaults.CompositeScheme);
                })
                .Configure(app =>
                {
                    app.UseAuthentication();
                    app.Run(async context =>
                    {
                        var identity = context.User.Identity;
                        await context.Response.WriteAsync(
                            $"{identity?.IsAuthenticated == true}|{identity?.Name ?? "<none>"}");
                    });
                }))
            .StartAsync();
    }

    private static async Task<string> ProbeAsync(IHost host, string? credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        if (credential is not null)
            request.Headers.Add(Header, credential);

        var response = await host.GetTestClient().SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task An_accepted_credential_authenticates_the_request()
    {
        using var host = await StartHostAsync();

        (await ProbeAsync(host, "valid")).Should().Be("True|probe-user");
    }

    [Fact]
    public async Task No_credential_leaves_the_request_unauthenticated()
    {
        using var host = await StartHostAsync();

        (await ProbeAsync(host, credential: null)).Should().Be("False|<none>");
    }

    /// <summary>
    /// The documented equivalence, asserted as an equivalence rather than as two separate facts —
    /// which is the only way it can fail meaningfully. If ASP.NET ever assigned a principal for a
    /// failed result, "rejected" and "anonymous" would stop being the same request downstream and
    /// the guide's outcome table would be wrong.
    /// </summary>
    [Fact]
    public async Task A_refused_credential_is_indistinguishable_from_no_credential()
    {
        using var host = await StartHostAsync();

        var refused = await ProbeAsync(host, "forged");
        var anonymous = await ProbeAsync(host, credential: null);

        refused.Should().Be(anonymous,
            "AuthenticateResult.Fail carries no principal, so the middleware leaves HttpContext.User "
            + "untouched — a rejected credential therefore reaches the endpoint as anonymity, and is "
            + "authorized as anonymity. Only the composite handler's warning records that anything "
            + "was presented at all");
        refused.Should().Be("False|<none>");
    }
}
