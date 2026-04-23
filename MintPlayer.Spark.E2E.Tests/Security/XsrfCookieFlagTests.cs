using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// L-2 — the XSRF-TOKEN cookie must carry the <c>Secure</c> attribute when the response is
/// served over HTTPS, and must pin <c>SameSite=Strict</c>. The fixture runs Fleet over
/// https://localhost:{random-port}, so every response should mint a cookie with both.
/// HttpOnly is intentionally false (double-submit pattern requires JS to read the cookie)
/// and is not asserted here.
///
/// Uses <see cref="SparkClientFactory.CreateHttpClient"/> instead of <see cref="SparkClient"/>
/// proper because the assertions inspect <c>Set-Cookie</c> *attributes* (Secure, SameSite) —
/// SparkClient's cookie jar parses name+value and drops the rest. A raw
/// <see cref="HttpClient"/> preserves the full header for inspection.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class XsrfCookieFlagTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public XsrfCookieFlagTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task XSRF_TOKEN_cookie_carries_Secure_attribute_over_https()
    {
        var cookie = await FetchXsrfCookieAsync();
        cookie.Should().NotBeNull("server should mint an XSRF-TOKEN cookie for the SPA to echo back");
        HasAttribute(cookie!, "Secure").Should().BeTrue(
            $"over HTTPS the XSRF-TOKEN cookie must set the Secure attribute. Actual cookie: [{cookie}]");
    }

    [Fact]
    public async Task XSRF_TOKEN_cookie_has_SameSite_Strict()
    {
        var cookie = await FetchXsrfCookieAsync();
        cookie.Should().NotBeNull();
        GetAttributeValue(cookie!, "SameSite").Should().BeEquivalentTo("Strict",
            $"SameSite=Strict is the documented defence against cross-site CSRF for the double-submit token. " +
            $"Actual cookie: [{cookie}]");
    }

    /// <summary>
    /// Returns the full Set-Cookie line for the XSRF-TOKEN cookie, or null if absent.
    /// <see cref="HttpResponseMessage.Headers"/> exposes each Set-Cookie as its own value;
    /// a multi-value match yields multiple strings, so we scan for the one starting with
    /// <c>XSRF-TOKEN=</c>.
    /// </summary>
    private async Task<string?> FetchXsrfCookieAsync()
    {
        using var http = SparkClientFactory.CreateHttpClient(_fixture.Host);
        using var response = await http.GetAsync("/spark/auth/me");
        ((int)response.StatusCode).Should().Be(200);

        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            return null;

        return setCookies.FirstOrDefault(line =>
            line.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
    }

    /// <summary>Case-insensitive "does this cookie have the given flag attribute?" (e.g. Secure, HttpOnly).</summary>
    private static bool HasAttribute(string cookie, string name) =>
        cookie.Split(';')
            .Select(part => part.Trim())
            .Any(part => string.Equals(part, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Case-insensitive attribute key lookup; returns the attribute's value verbatim or null.</summary>
    private static string? GetAttributeValue(string cookie, string name)
    {
        foreach (var part in cookie.Split(';').Select(p => p.Trim()))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (string.Equals(part[..eq], name, StringComparison.OrdinalIgnoreCase))
                return part[(eq + 1)..];
        }
        return null;
    }
}
