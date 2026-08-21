using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// Pins <see cref="SparkLocalCredentials"/> — which auth endpoints each mode mounts.
/// </summary>
/// <remarks>
/// These assert over <see cref="EndpointDataSource"/> rather than over HTTP status codes, and the
/// distinction is the whole point of the requirement: a 404 only proves an endpoint is unreachable
/// on one path through the pipeline, whereas the requirement is that it is <em>absent</em> from the
/// route table. A status-code assertion would pass just as happily against shadowing middleware,
/// which is the design this feature exists to avoid.
/// </remarks>
public class LocalCredentialModeTests : SparkTestDriver
{
    private const string StubProvider = "StubProvider";

    /// <summary>
    /// Stands in for GitHub. Returns a fixed external identity so the callback's provisioning branch
    /// is reachable without a real OAuth round trip, including the issuer-attested email_verified
    /// claim the callback requires before it will auto-create an account.
    /// </summary>
    private sealed class StubExternalHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string Email = "external-only@example.com";
        internal const string ProviderKey = "stub-provider-key-1";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ProviderKey),
                new Claim(ClaimTypes.Email, Email),
                new Claim("email_verified", "true"),
            ], StubProvider);

            var properties = new AuthenticationProperties();
            properties.Items[".AuthScheme"] = StubProvider;
            properties.Items["LoginProvider"] = StubProvider;

            var ticket = new AuthenticationTicket(
                new ClaimsPrincipal(identity), properties, IdentityConstants.ExternalScheme);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private async Task<IHost> StartAsync(SparkLocalCredentials mode, bool withExternalProvider = true)
    {
        return await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();

                    if (withExternalProvider)
                    {
                        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, StubExternalHandler>(
                            StubProvider, displayName: "Stub Provider", configureOptions: null);
                    }

                    services.AddAuthorization();
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapSparkIdentityApi<SparkUser>(mode);

                        // Stands in for the provider's OAuth handler. A real handler (AddGitHub,
                        // AddGoogle, …) sets SignInScheme = IdentityConstants.ExternalScheme and
                        // signs the provider identity into that cookie before redirecting to the
                        // callback; this does exactly that and nothing else, so the callback under
                        // test sees precisely what it would see in production.
                        endpoints.MapGet("/test/external-handshake", async context =>
                        {
                            var identity = new ClaimsIdentity(
                            [
                                new Claim(ClaimTypes.NameIdentifier, StubExternalHandler.ProviderKey),
                                new Claim(ClaimTypes.Email, StubExternalHandler.Email),
                                new Claim("email_verified", "true"),
                            ], StubProvider);

                            var properties = new AuthenticationProperties();
                            properties.Items["LoginProvider"] = StubProvider;

                            await context.SignInAsync(
                                IdentityConstants.ExternalScheme,
                                new ClaimsPrincipal(identity),
                                properties);
                        });
                    });
                }))
            .StartAsync();
    }

    /// <summary>Route patterns paired with their HTTP methods — /manage/info needs the method to be distinguishable.</summary>
    private static IReadOnlyList<string> RoutesOf(IHost host) =>
        [.. host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint =>
            {
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
                return $"{string.Join(",", methods.Order(StringComparer.Ordinal))} {endpoint.RoutePattern.RawText}";
            })
            .Order(StringComparer.OrdinalIgnoreCase)];

    private static readonly string[] LocalCredentialRoutes =
    [
        "/spark/auth/register",
        "/spark/auth/login",
        "/spark/auth/refresh",
        "/spark/auth/confirmEmail",
        "/spark/auth/resendConfirmationEmail",
        "/spark/auth/forgotPassword",
        "/spark/auth/resetPassword",
    ];

    [Fact]
    public async Task Full_mode_matches_the_pre_change_endpoint_set()
    {
        // A golden list captured from master before the option existed. This is the test that goes
        // red if a mode's filter ever leaks into the default — the failure nobody would notice by
        // running an external-only app.
        using var host = await StartAsync(SparkLocalCredentials.Full);

        var routes = RoutesOf(host);

        foreach (var route in LocalCredentialRoutes)
            routes.Should().Contain(r => r.EndsWith($" {route}", StringComparison.OrdinalIgnoreCase));

        routes.Should().Contain(r => r.EndsWith(" /spark/auth/manage/2fa", StringComparison.OrdinalIgnoreCase));
        routes.Should().Contain("GET /spark/auth/manage/info");
        routes.Should().Contain("POST /spark/auth/manage/info");
    }

    [Fact]
    public async Task Disabled_mode_does_not_map_any_local_credential_endpoint()
    {
        using var host = await StartAsync(SparkLocalCredentials.Disabled);

        var routes = RoutesOf(host);

        foreach (var route in LocalCredentialRoutes)
            routes.Should().NotContain(r => r.EndsWith($" {route}", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Disabled_mode_removes_post_manage_info_but_keeps_get()
    {
        // The POST rotates the email the external binding was provisioned against, which would
        // desynchronize it from the issuer-attested claim. GET only reads, so it stays.
        using var host = await StartAsync(SparkLocalCredentials.Disabled);

        var routes = RoutesOf(host);

        routes.Should().Contain("GET /spark/auth/manage/info");
        routes.Should().NotContain("POST /spark/auth/manage/info");
    }

    [Fact]
    public async Task Disabled_mode_keeps_the_universal_auth_endpoints()
    {
        // /csrf-refresh is the load-bearing one: without it the XSRF cookie is never rotated after
        // sign-in, and every subsequent mutating Spark call fails antiforgery.
        using var host = await StartAsync(SparkLocalCredentials.Disabled);

        var routes = RoutesOf(host);

        routes.Should().Contain(r => r.EndsWith(" /spark/auth/me", StringComparison.OrdinalIgnoreCase));
        routes.Should().Contain(r => r.EndsWith(" /spark/auth/logout", StringComparison.OrdinalIgnoreCase));
        routes.Should().Contain(r => r.EndsWith(" /spark/auth/csrf-refresh", StringComparison.OrdinalIgnoreCase));
        routes.Should().Contain(r => r.EndsWith(" /spark/auth/external-login", StringComparison.OrdinalIgnoreCase));
        routes.Should().Contain(r => r.EndsWith(" /spark/auth/external-login-callback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SignInOnly_removes_only_self_service_registration()
    {
        // Admin-provisioned accounts still need password sign-in and password recovery.
        using var host = await StartAsync(SparkLocalCredentials.SignInOnly);

        var routes = RoutesOf(host);

        routes.Should().NotContain(r => r.EndsWith(" /spark/auth/register", StringComparison.OrdinalIgnoreCase));
        routes.Should().NotContain(r => r.EndsWith(" /spark/auth/resendConfirmationEmail", StringComparison.OrdinalIgnoreCase));

        routes.Should().Contain(r => r.EndsWith(" /spark/auth/login", StringComparison.OrdinalIgnoreCase));
        routes.Should().Contain(r => r.EndsWith(" /spark/auth/forgotPassword", StringComparison.OrdinalIgnoreCase));
        routes.Should().Contain(r => r.EndsWith(" /spark/auth/resetPassword", StringComparison.OrdinalIgnoreCase));
        routes.Should().Contain(r => r.EndsWith(" /spark/auth/confirmEmail", StringComparison.OrdinalIgnoreCase));
        routes.Should().Contain("POST /spark/auth/manage/info");
    }

    [Fact]
    public async Task Antiforgery_metadata_survives_the_filtered_republication()
    {
        // The filter re-publishes endpoints through a second data source. If the route-group
        // conventions were lost in that hop, Spark would silently stop enforcing double-submit CSRF
        // on the endpoints it kept — a worse outcome than the mapper being unfilterable at all.
        using var host = await StartAsync(SparkLocalCredentials.Disabled);

        var manageInfoGet = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText!.EndsWith("/manage/info", StringComparison.OrdinalIgnoreCase));

        var twoFactor = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText!.EndsWith("/manage/2fa", StringComparison.OrdinalIgnoreCase));

        // 2fa is a POST, so it is stamped; the surviving manage/info is the GET, which is not.
        twoFactor.Metadata.GetMetadata<Microsoft.AspNetCore.Antiforgery.IAntiforgeryMetadata>()
            .Should().NotBeNull("the CSRF stamping must survive re-publication");
        manageInfoGet.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Should().Contain("GET");
    }

    [Fact]
    public async Task Identity_services_are_registered_in_every_mode()
    {
        // The mode gates routes, never services. AddIdentityApiEndpoints is what makes UserManager
        // and SignInManager resolvable, and the external-login callback depends on both — so
        // "clean up" that call and external login breaks while every route test still passes.
        using var host = await StartAsync(SparkLocalCredentials.Disabled);
        using var scope = host.Services.CreateScope();

        scope.ServiceProvider.GetService<UserManager<SparkUser>>().Should().NotBeNull();
        scope.ServiceProvider.GetService<SignInManager<SparkUser>>().Should().NotBeNull();
    }

    [Fact]
    public async Task Disabled_without_an_external_provider_throws_on_startup()
    {
        // Booting anyway would produce an application that looks healthy and that nobody can sign
        // into, with a symptom (a sign-in page with no buttons) that points nowhere near the cause.
        var start = async () => await StartAsync(SparkLocalCredentials.Disabled, withExternalProvider: false);

        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no external authentication provider is registered*");
    }

    [Fact]
    public async Task Disabled_with_an_external_provider_starts()
    {
        // The discriminating half of the guard test: the throw above must be caused by the missing
        // provider, not by Disabled mode being broken outright.
        using var host = await StartAsync(SparkLocalCredentials.Disabled, withExternalProvider: true);

        RoutesOf(host).Should().NotBeEmpty();
    }

    [Fact]
    public async Task External_login_provisions_a_user_when_local_credentials_are_disabled()
    {
        // SPIKE S2, landed as a permanent test. F3 argued from inspection that the callback never
        // touches /register or /confirmEmail; this observes it. A framework flag that silently broke
        // the only remaining way to sign in would be worse than the problem it fixes.
        using var host = await StartAsync(SparkLocalCredentials.Disabled);
        var server = host.GetTestServer();
        using var client = server.CreateClient();

        // The provider hands us the external cookie...
        var handshake = await client.GetAsync("/test/external-handshake");
        var externalCookie = handshake.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? string.Join("; ", cookies.Select(c => c.Split(';')[0]))
            : null;
        externalCookie.Should().NotBeNull("the stub handler must issue Identity's external cookie");

        // ...and the callback turns it into an account.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/spark/auth/external-login-callback?returnUrl=%2F");
        request.Headers.Add("Cookie", externalCookie);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.OK);

        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SparkUser>>();
        var user = await users.FindByEmailAsync(StubExternalHandler.Email);

        user.Should().NotBeNull("the callback provisions the account itself, without /register");
        user!.EmailConfirmed.Should().BeTrue("the callback confirms the attested email, without /confirmEmail");
        (await users.HasPasswordAsync(user)).Should().BeFalse("an externally-provisioned user has no password");
        (await users.GetLoginsAsync(user)).Should().Contain(login => login.LoginProvider == StubProvider);
    }
}
