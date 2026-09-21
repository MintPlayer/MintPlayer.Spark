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
        response.StatusCode.Should().BeOneOf([HttpStatusCode.Redirect, HttpStatusCode.Found],
            "callback should respond with a redirect, not embed the returnUrl in HTML");

        var location = response.Headers.Location?.ToString() ?? string.Empty;

        // ⚠️ Asserted on the PATH, not on the whole header, and that is a deliberate loosening —
        // made once, with a reason. A refused sign-in now carries why it was refused
        // (`?sparkExternalLogin=<code>`), so the location is "/?sparkExternalLogin=…" rather than
        // bare "/". The security property was never "the header equals a slash"; it is "the
        // destination is the sanitized default and nothing the caller supplied survives into it",
        // and the two assertions below say exactly that instead of implying it.
        PathOf(location).Should().Be("/",
            $"hostile returnUrl '{hostileReturnUrl}' must be substituted with the default");

        foreach (var trace in new[] { "attacker.example", "javascript:", "\r", "\n" })
        {
            location.Should().NotContain(trace,
                "nothing from the hostile returnUrl may survive into the Location header");
        }
    }

    /// <summary>
    /// The redirect target without its query string. The query now legitimately carries the
    /// outcome code, which is not part of where the browser is being sent.
    /// </summary>
    private static string PathOf(string location)
    {
        var query = location.IndexOf('?');
        return query < 0 ? location : location[..query];
    }

    [Fact]
    public async Task External_login_callback_preserves_safe_local_returnUrl()
    {
        using var http = CreateNonFollowingClient();
        var encoded = Uri.EscapeDataString("/dashboard");

        var response = await http.GetAsync($"/spark/auth/external-login-callback?returnUrl={encoded}");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found);

        // The half of this pair that actually guards against a regression: a safe path must still
        // be honoured, not quietly replaced by the default. If the outcome code were ever appended
        // to "/" instead of to the caller's path, the test above would still pass and this one
        // would fail — which is why both exist.
        var location = response.Headers.Location?.ToString() ?? string.Empty;
        PathOf(location).Should().Be("/dashboard", "in-app paths must be preserved");
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
