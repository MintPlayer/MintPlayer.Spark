using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// Pins <see cref="SparkAuthenticationExtensions.MapSparkIdentityApi{TUser}"/> — the
/// endpoint-mapping side of Spark auth. Exercises the registration code path (every
/// route in the group has to map without exceptions) and the external-login HTTP
/// surface that lives directly inside the extension method (the OAuth-challenge
/// endpoint and the callback's null-info early-return). The deeper handler branches
/// (Succeeded vs. first-time create) need a real OAuth round-trip and are covered
/// by the Fleet E2E suite.
/// </summary>
public class MapSparkIdentityApiTests : SparkTestDriver
{
    private async Task<TestServer> StartHostAsync()
    {
        // Register a real cookie scheme so Results.Challenge in the /external-login handler
        // resolves to a 302 redirect instead of throwing on an unregistered provider name.
        const string testScheme = "TestExternal";

        var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();

                    // Append a cookie-backed external scheme that the SignInManager can
                    // challenge against without us having to mount a real OAuth handler.
                    services.AddAuthentication()
                        .AddCookie(testScheme, opts =>
                        {
                            opts.LoginPath = "/external-stub-login";
                        });
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
                        endpoints.MapSparkIdentityApi<SparkUser>();
                    });
                }))
            .StartAsync();

        return host.GetTestServer();
    }

    [Fact]
    public async Task MapSparkIdentityApi_boots_without_exceptions_and_serves_404_for_unmapped_paths()
    {
        // Smoke: the call chain MapGroup → MapIdentityApi<TUser>() → MapSparkAuthEndpoints()
        // touches a lot of source-generated and library-internal code at startup. If any
        // step throws, the entire auth surface is broken — covering the boot path is itself
        // worth a test.
        using var server = await StartHostAsync();
        using var client = server.CreateClient();

        var response = await client.GetAsync("/no-such-route");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ExternalLogin_endpoint_returns_302_to_the_external_scheme_login_path()
    {
        // Hits the GET /spark/auth/external-login handler — exercises the
        // ConfigureExternalAuthenticationProperties + Results.Challenge path. With cookie
        // as the registered scheme, the challenge writes a 302 to the scheme's LoginPath.
        using var server = await StartHostAsync();
        using var client = server.CreateClient();
        client.DefaultRequestVersion = HttpVersion.Version11;

        var response = await client.GetAsync("/spark/auth/external-login?provider=TestExternal&returnUrl=%2Fhome");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        // Cookie auth's challenge redirects to LoginPath with a returnUrl carrying the
        // callback URL we configured in the handler.
        response.Headers.Location!.OriginalString.Should().Contain("/external-stub-login");
        response.Headers.Location!.OriginalString.Should().Contain("external-login-callback");
    }

    [Fact]
    public async Task ExternalLogin_carries_the_popup_flag_through_to_the_callback_url()
    {
        // M2: the callback's postMessage branch keys off ?popup, and /external-login was the
        // only thing that could ever set it — but it did not even accept the parameter, so the
        // branch was unreachable in production. Assert the propagation itself, not merely that
        // some callback URL was built.
        using var server = await StartHostAsync();
        using var client = server.CreateClient();
        client.DefaultRequestVersion = HttpVersion.Version11;

        var response = await client.GetAsync("/spark/auth/external-login?provider=TestExternal&returnUrl=%2Fhome&popup=1");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        // The callback URL is nested inside the cookie scheme's own ReturnUrl, so it arrives
        // double-encoded; decode once to read the callback query as the callback will see it.
        var callbackUrl = Uri.UnescapeDataString(response.Headers.Location!.OriginalString);
        callbackUrl.Should().Contain("external-login-callback");
        callbackUrl.Should().Contain("popup=1");
    }

    [Fact]
    public async Task ExternalLogin_omits_the_popup_flag_when_the_caller_did_not_ask_for_one()
    {
        // The other half: a full-page flow must not acquire a popup callback, or it would
        // land on a page that posts a message to an opener that does not exist and then
        // closes itself, instead of redirecting the user back into the app.
        using var server = await StartHostAsync();
        using var client = server.CreateClient();
        client.DefaultRequestVersion = HttpVersion.Version11;

        var response = await client.GetAsync("/spark/auth/external-login?provider=TestExternal&returnUrl=%2Fhome");

        var callbackUrl = Uri.UnescapeDataString(response.Headers.Location!.OriginalString);
        callbackUrl.Should().NotContain("popup");
    }

    [Fact]
    public async Task ExternalLoginCallback_reports_a_cancelled_login_to_the_popup_opener()
    {
        // No external auth cookie is what a provider-side cancellation looks like from here.
        // In a popup that must come back as a message: a redirect would navigate the popup
        // to the app, leaving the opener's listener waiting on a window nobody will close.
        using var server = await StartHostAsync();
        using var client = server.CreateClient();

        var response = await client.GetAsync("/spark/auth/external-login-callback?popup=1&returnUrl=%2Fhome");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("'spark:external-login'");
        body.Should().Contain("success: false");
        body.Should().Contain("no_login_info");
    }

    [Fact]
    public async Task ExternalLoginCallback_with_no_external_info_redirects_to_returnUrl()
    {
        // No external auth cookie → SignInManager.GetExternalLoginInfoAsync() returns null →
        // handler short-circuits with a redirect to the requested returnUrl.
        using var server = await StartHostAsync();
        using var client = server.CreateClient();

        // Disable auto-redirect so we can inspect the 302 response itself.
        var handler = server.CreateHandler();
        using var manualClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };

        var response = await manualClient.GetAsync("/spark/auth/external-login-callback?returnUrl=%2Fhome");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/home");
    }

    [Fact]
    public async Task ExternalLoginCallback_with_no_external_info_and_no_returnUrl_redirects_to_root()
    {
        using var server = await StartHostAsync();
        using var client = server.CreateClient();

        var response = await client.GetAsync("/spark/auth/external-login-callback");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/");
    }
}
