using System.Net;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// R2-C4 / R2-M3 — the /spark/auth/external-login-callback handler used to interpolate
/// returnUrl directly into a JS string literal inside a server-issued HTML page,
/// opening XSS (and open-redirect for non-popup flows). The fix is a server-side
/// Results.Redirect through SanitizeReturnUrl (relative paths only, no '//', no
/// '/\', no CR/LF). The challenge entry-point (R2-M3) applies the same sanitizer.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class ExternalLoginReturnUrlTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public ExternalLoginReturnUrlTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    private HttpClient CreateNonFollowingClient()
    {
        // Disable redirect follow so we can inspect the Location header directly.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            AllowAutoRedirect = false,
        };
        return new HttpClient(handler) { BaseAddress = new Uri(_fixture.Host.FleetUrl) };
    }

    [Theory]
    [InlineData("//attacker.example/phish")]
    [InlineData("/\\attacker.example/phish")]
    [InlineData("https://attacker.example")]
    [InlineData("http://attacker.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/path\r\nLocation: http://attacker.example")]
    public async Task External_login_callback_substitutes_default_for_non_local_returnUrl(string hostileReturnUrl)
    {
        using var http = CreateNonFollowingClient();
        var encoded = Uri.EscapeDataString(hostileReturnUrl);

        var response = await http.GetAsync($"/spark/auth/external-login-callback?returnUrl={encoded}");

        // The callback redirects to the sanitized URL. Without an OAuth session,
        // signInManager.GetExternalLoginInfoAsync returns null → fallback redirect
        // path. The fallback MUST be the sanitized value, which is "/".
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found,
            "callback should respond with a redirect, not embed the returnUrl in HTML");

        var location = response.Headers.Location?.ToString() ?? string.Empty;
        location.Should().Be("/",
            $"hostile returnUrl '{hostileReturnUrl}' must be substituted with the default");
    }

    [Fact]
    public async Task External_login_callback_preserves_safe_local_returnUrl()
    {
        using var http = CreateNonFollowingClient();
        var encoded = Uri.EscapeDataString("/dashboard");

        var response = await http.GetAsync($"/spark/auth/external-login-callback?returnUrl={encoded}");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found);
        response.Headers.Location?.ToString().Should().Be("/dashboard",
            "in-app paths must be preserved");
    }

    [Theory]
    [InlineData("//attacker.example")]
    [InlineData("https://attacker.example")]
    public async Task External_login_challenge_substitutes_default_for_non_local_returnUrl(string hostileReturnUrl)
    {
        using var http = CreateNonFollowingClient();
        // The challenge endpoint with a non-existent provider will fail before
        // OAuth runs, but it will have already passed returnUrl through
        // SanitizeReturnUrl and embedded the safe value in callbackUrl. We can
        // detect the sanitized value by checking that the redirect's callback
        // parameter doesn't contain the hostile host. The challenge response
        // varies (302 to provider, or 500 if provider missing); the key
        // assertion is that the response body / Location does NOT echo the
        // attacker URL verbatim.
        var response = await http.GetAsync($"/spark/auth/external-login?provider=GitHub&returnUrl={Uri.EscapeDataString(hostileReturnUrl)}");
        var bodyOrLocation = (response.Headers.Location?.ToString() ?? string.Empty)
            + " " + await response.Content.ReadAsStringAsync();

        bodyOrLocation.Should().NotContain("attacker.example",
            "challenge must sanitize returnUrl before embedding it in OAuth state");
    }
}
