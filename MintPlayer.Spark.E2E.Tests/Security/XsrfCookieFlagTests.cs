using System.Linq;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// L-2 — the XSRF-TOKEN cookie must carry the <c>Secure</c> attribute when the response
/// is served over HTTPS. The fixture runs Fleet over https://localhost:{random-port}, so
/// every response should mint a Secure cookie. HttpOnly is intentionally false (double-
/// submit pattern requires JS to read the cookie) and is not asserted here.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class XsrfCookieFlagTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public XsrfCookieFlagTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task XSRF_TOKEN_cookie_carries_Secure_attribute_over_https()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        // Any response triggers the antiforgery middleware — /spark/auth/me is cheap.
        var response = await page.APIRequest.GetAsync($"{_fixture.Host.FleetUrl}/spark/auth/me");
        response.Status.Should().Be(200);

        var setCookies = response.Headers
            .Where(h => string.Equals(h.Key, "set-cookie", StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Value)
            .ToList();
        var xsrfCookie = setCookies.FirstOrDefault(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));

        xsrfCookie.Should().NotBeNull("server should mint an XSRF-TOKEN cookie for the SPA to echo back");
        xsrfCookie!.ToLowerInvariant().Should().Contain("secure",
            "over HTTPS the XSRF-TOKEN cookie must set the Secure attribute so it is never sent over plain HTTP");
    }

    [Fact]
    public async Task XSRF_TOKEN_cookie_has_SameSite_Strict()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        var response = await page.APIRequest.GetAsync($"{_fixture.Host.FleetUrl}/spark/auth/me");
        response.Status.Should().Be(200);

        var setCookies = response.Headers
            .Where(h => string.Equals(h.Key, "set-cookie", StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Value)
            .ToList();
        var xsrfCookie = setCookies.FirstOrDefault(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));

        xsrfCookie.Should().NotBeNull();
        xsrfCookie!.Should().Contain("SameSite=Strict",
            "SameSite=Strict is the documented defence against cross-site CSRF for the double-submit token");
    }
}
