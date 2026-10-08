using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// #490 M1 — the server half of the PWA-safe external-login hand-off: the challenge's nonce rule,
/// the flags it forwards into the callback URL, and the popup callback page those flags select.
/// </summary>
/// <remarks>
/// The page's script itself runs in a browser and is exercised by the Fleet E2E suite; what is
/// pinned here is everything the server decides — what reaches the page, and how it is encoded.
/// </remarks>
public class ExternalLoginHandoffTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private const string TestScheme = "TestExternal";
    private const string ValidNonce = "AbCdEfGhIjKlMnOpQrStUvWxYz012-_9";

    private async Task<TestServer> StartHostAsync(bool appSendsCoop = false)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();
                    services.Configure<SparkAuthenticationOptions>(o => o.LocalCredentials = SparkLocalCredentials.Full);
                    services.AddTestMailSink();
                    // A cookie scheme stands in for a provider: its challenge is a 302 to LoginPath
                    // carrying the callback URL, which is exactly the part under test.
                    services.AddAuthentication().AddCookie(TestScheme, o => o.LoginPath = "/external-stub-login");
                    services.AddAuthorization();
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    if (appSendsCoop)
                    {
                        // An application-wide header middleware, the way an app might harden itself.
                        app.Use(async (context, next) =>
                        {
                            context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
                            await next();
                        });
                    }
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapSparkIdentityApi<SparkUser>());
                }))
            .StartAsync();

        return host.GetTestServer();
    }

    /// <summary>The callback URL's query, as the callback request will see it.</summary>
    private static Dictionary<string, Microsoft.Extensions.Primitives.StringValues> CallbackQueryOf(HttpResponseMessage challenge)
    {
        challenge.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = challenge.Headers.Location!.OriginalString;
        var stubQuery = QueryHelpers.ParseQuery(location[location.IndexOf('?')..]);
        var callbackUrl = stubQuery["ReturnUrl"].ToString();
        callbackUrl.Should().Contain("/spark/auth/external-login-callback");
        return QueryHelpers.ParseQuery(callbackUrl[callbackUrl.IndexOf('?')..]);
    }

    // --- challenge --------------------------------------------------------

    [Fact]
    public async Task A_valid_nonce_is_forwarded_to_the_callback_url_with_ngsw_bypass()
    {
        using var server = await StartHostAsync();
        using var client = server.CreateClient();

        var response = await client.GetAsync(
            $"/spark/auth/external-login?provider={TestScheme}&returnUrl=%2Fhome&popup=1&nonce={ValidNonce}&ngsw-bypass=true");

        var query = CallbackQueryOf(response);
        query["popup"].ToString().Should().Be("1");
        query["nonce"].ToString().Should().Be(ValidNonce);
        query["ngsw-bypass"].ToString().Should().Be("true");
        query["returnUrl"].ToString().Should().Be("/home");
    }

    [Fact]
    public async Task A_missing_nonce_keeps_the_legacy_callback_url_but_still_bypasses_the_service_worker()
    {
        using var server = await StartHostAsync();
        using var client = server.CreateClient();

        var response = await client.GetAsync($"/spark/auth/external-login?provider={TestScheme}&returnUrl=%2Fhome&popup=1");

        var query = CallbackQueryOf(response);
        query.Should().NotContainKey("nonce");
        query["popup"].ToString().Should().Be("1");
        query["ngsw-bypass"].ToString().Should().Be("true");
    }

    [Theory]
    [InlineData("tooshort")]
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz0123456789AbCdEfGhIjKlMnOpQrStUvWxYz0123")] // 66 characters
    [InlineData("AbCdEfGhIjKlMnOp'\"</script><script>alert(1)</script>")]
    [InlineData("AbCdEfGhIjKlMnOp+/==")] // base64, not base64url
    [InlineData("AbCdEfGhIjKlMnOpQrSt\n")]
    public async Task An_invalid_nonce_is_refused_with_invalid_nonce(string nonce)
    {
        using var server = await StartHostAsync();
        using var client = server.CreateClient();

        var response = await client.GetAsync(
            $"/spark/auth/external-login?provider={TestScheme}&returnUrl=%2Fhome&popup=1&nonce={Uri.EscapeDataString(nonce)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_nonce");
    }

    // --- errorUrl (#490 M6) -------------------------------------------------

    [Fact]
    public async Task An_error_url_is_forwarded_to_the_callback_url()
    {
        using var server = await StartHostAsync();
        using var client = server.CreateClient();
        var errorUrl = "/sign-in?returnUrl=%2Fhome";

        var response = await client.GetAsync(
            $"/spark/auth/external-login?provider={TestScheme}&returnUrl=%2Fhome&errorUrl={Uri.EscapeDataString(errorUrl)}");

        var query = CallbackQueryOf(response);
        query["errorUrl"].ToString().Should().Be(errorUrl);
        query["returnUrl"].ToString().Should().Be("/home");
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    public async Task A_hostile_error_url_is_sanitized_at_the_challenge(string errorUrl)
    {
        using var server = await StartHostAsync();
        using var client = server.CreateClient();

        var response = await client.GetAsync(
            $"/spark/auth/external-login?provider={TestScheme}&returnUrl=%2Fhome&errorUrl={Uri.EscapeDataString(errorUrl)}");

        CallbackQueryOf(response)["errorUrl"].ToString().Should().Be("/");
    }

    [Fact]
    public async Task Without_an_error_url_the_callback_url_carries_none()
    {
        using var server = await StartHostAsync();
        using var client = server.CreateClient();

        var response = await client.GetAsync($"/spark/auth/external-login?provider={TestScheme}&returnUrl=%2Fhome");

        CallbackQueryOf(response).Should().NotContainKey("errorUrl");
    }

    [Fact]
    public async Task A_redirect_mode_failure_lands_on_the_error_url_with_the_code()
    {
        using var server = await StartHostAsync();
        using var client = server.CreateClient();

        // No external cookie: the callback refuses with no_login_info.
        var response = await client.GetAsync(
            $"/spark/auth/external-login-callback?returnUrl=%2Fhome&errorUrl={Uri.EscapeDataString("/sign-in?returnUrl=%2Fhome")}");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/sign-in?returnUrl=%2Fhome&sparkExternalLogin=no_login_info");
    }

    [Fact]
    public async Task A_redirect_mode_failure_without_an_error_url_still_lands_on_the_return_url()
    {
        using var server = await StartHostAsync();
        using var client = server.CreateClient();

        var response = await client.GetAsync("/spark/auth/external-login-callback?returnUrl=%2Fhome");

        response.Headers.Location!.OriginalString.Should().Be("/home?sparkExternalLogin=no_login_info");
    }

    [Fact]
    public async Task A_hostile_error_url_on_the_callback_is_sanitized()
    {
        using var server = await StartHostAsync();
        using var client = server.CreateClient();

        var response = await client.GetAsync(
            $"/spark/auth/external-login-callback?returnUrl=%2Fhome&errorUrl={Uri.EscapeDataString("https://evil.example/x")}");

        response.Headers.Location!.OriginalString.Should().Be("/?sparkExternalLogin=no_login_info");
    }

    // --- callback page ----------------------------------------------------

    [Fact]
    public async Task The_popup_callback_page_carries_the_json_encoded_payload_and_the_channel_contract()
    {
        using var server = await StartHostAsync();
        using var client = server.CreateClient();

        // No external cookie: the callback reports no_login_info, which is enough to see the page.
        var response = await client.GetAsync(
            $"/spark/auth/external-login-callback?returnUrl=%2Fhome&popup=1&nonce={ValidNonce}&ngsw-bypass=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain($$"""{"type":"spark:external-login","success":false,"error":"no_login_info","nonce":"{{ValidNonce}}"}""");
        body.Should().Contain("new BroadcastChannel(channelName)");
        body.Should().Contain("var channelName = 'spark:external-login';");
        body.Should().Contain("'spark:external-login:' + msg.nonce");
        body.Should().Contain("'spark:external-login-done:' + msg.nonce");
        body.Should().Contain("d.type === 'spark:external-login-ack' && d.nonce === msg.nonce");
        body.Should().Contain("window.opener.postMessage(msg, window.location.origin)");
        // F12: standalone closes straight after writing the channels, without waiting for an ack.
        body.Should().Contain("matchMedia('(display-mode: standalone)')");
        body.Should().Contain("if (standalone) window.close();");
        body.Should().Contain("var returnUrl = \"/home\";");
        body.Should().Contain("Sign-in failed — you can close this window.");
        body.Should().Contain("id=\"spark-external-login-close\"");
    }

    [Fact]
    public async Task The_restore_target_is_the_sanitized_return_url_never_the_raw_one()
    {
        using var server = await StartHostAsync();
        using var client = server.CreateClient();

        var response = await client.GetAsync(
            $"/spark/auth/external-login-callback?returnUrl={Uri.EscapeDataString("https://evil.example/x")}&popup=1&nonce={ValidNonce}");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("var returnUrl = \"/\";");
        body.Should().NotContain("evil.example");
    }

    /// <summary>
    /// The callback is anonymous, so its URL can be typed by anyone: the challenge's check is not
    /// what protects it. A malformed nonce there is dropped, never echoed.
    /// </summary>
    [Fact]
    public async Task A_hostile_nonce_on_the_callback_never_reaches_the_page()
    {
        using var server = await StartHostAsync();
        using var client = server.CreateClient();
        const string hostile = "AbCdEfGhIjKlMnOp'\"</script><script>alert(1)</script>";

        var response = await client.GetAsync(
            $"/spark/auth/external-login-callback?returnUrl=%2Fhome&popup=1&nonce={Uri.EscapeDataString(hostile)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("alert(1)");
        body.Should().NotContain("AbCdEfGhIjKlMnOp");
        body.Should().Contain("\"nonce\":null");
        body.Should().NotContain("BroadcastChannel");
    }

    [Fact]
    public async Task The_popup_callback_response_carries_no_cross_origin_opener_policy()
    {
        using var server = await StartHostAsync();
        using var client = server.CreateClient();

        var response = await client.GetAsync($"/spark/auth/external-login-callback?returnUrl=%2Fhome&popup=1&nonce={ValidNonce}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Cross-Origin-Opener-Policy").Should().BeFalse();
    }

    /// <summary>
    /// D10: <c>same-origin</c> COOP severs <c>window.opener</c>, so an application-wide header
    /// would silently break every desktop popup sign-in. The callback page strips it.
    /// </summary>
    [Fact]
    public async Task The_popup_callback_strips_an_application_cross_origin_opener_policy()
    {
        using var server = await StartHostAsync(appSendsCoop: true);
        using var client = server.CreateClient();

        // Control: the header does arrive where this test looks for it, on a non-popup response.
        var redirect = await client.GetAsync("/spark/auth/external-login-callback?returnUrl=%2Fhome");
        redirect.Headers.Contains("Cross-Origin-Opener-Policy").Should().BeTrue();

        var response = await client.GetAsync($"/spark/auth/external-login-callback?returnUrl=%2Fhome&popup=1&nonce={ValidNonce}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Cross-Origin-Opener-Policy").Should().BeFalse();
    }

    // --- the page builder itself -------------------------------------------

    [Fact]
    public void A_successful_hand_off_reports_success_and_the_signed_in_fallback_text()
    {
        var html = SparkAuthenticationExtensions.ExternalLoginPopupHtml(ValidNonce, "/home", error: null);

        html.Should().Contain($$"""{"type":"spark:external-login","success":true,"error":null,"nonce":"{{ValidNonce}}"}""");
        html.Should().Contain("Signed in — you can close this window and return to your browser.");
    }

    [Fact]
    public void The_return_url_is_json_encoded_so_quotes_and_markup_cannot_leave_the_string()
    {
        var html = SparkAuthenticationExtensions.ExternalLoginPopupHtml(ValidNonce, "/a?b=1&c='x'</script>", error: null);

        html.Should().NotContain("'x'");
        html.Should().NotContain("c='x'</script>");
        html.Should().Contain("\\u0026");
        html.Should().Contain("\\u0027x\\u0027");
        // Exactly one closing tag: the page's own.
        html.Split("</script>").Length.Should().Be(2);
    }
}
