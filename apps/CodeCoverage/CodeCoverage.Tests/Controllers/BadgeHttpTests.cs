using System.Net;
using CodeCoverage.Tests._Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// The badge's response headers as they leave the <b>real</b> pipeline, not the controller.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BadgeControllerTests"/> calls the action directly and reads
/// <c>Response.Headers.CacheControl</c> off the controller. That proves what the action writes, and
/// nothing about what the client receives. Since #452 every response passes through
/// <c>UseAntiforgeryGenerator()</c>, which adds a per-user <c>Set-Cookie</c> and forces any
/// <c>Cache-Control</c> it finds to <c>private</c>. Without <c>[SkipXsrfToken]</c> the anonymous
/// badge would leave as <c>max-age=300, private</c>, and camo would stop caching it. The controller
/// tests would stay green throughout.
/// </para>
/// <para>
/// The control, <see cref="A_non_badge_response_still_carries_the_xsrf_cookie"/>, keeps the badge
/// assertions honest: a host that minted nothing at all would pass them too.
/// </para>
/// </remarks>
[Collection(CoverageWebHostCollection.Name)]
public class BadgeHttpTests
{
    private readonly CoverageWebHostFixture host;

    public BadgeHttpTests(CoverageWebHostFixture host) => this.host = host;

    private HttpClient Client()
        => host.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private static IReadOnlyList<string> SetCookies(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];

    [Fact]
    public async Task The_anonymous_badge_stays_shared_cacheable_and_carries_no_cookie()
    {
        using var client = Client();

        // A repository that does not exist: the badge still renders ("unknown"), never 404s, and
        // takes the same header path as a public repository (the never-404 rule).
        var response = await client.GetAsync("/badge/github/no-such-owner/no-such-repo.svg");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Asserted on the parsed directives: HttpClient re-serialises Cache-Control in its own
        // directive order, so comparing strings tests the formatter, not the header.
        var cacheControl = response.Headers.CacheControl!;
        cacheControl.Public.Should().BeTrue("a shared cache (camo) must be allowed to keep the anonymous badge");
        cacheControl.Private.Should().BeFalse("the mint forces private onto any response it adds a Set-Cookie to");
        cacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(300));
        SetCookies(response).Should().BeEmpty(
            "[SkipXsrfToken] keeps the badge out of the mint; a Set-Cookie would make it per-user");
        response.Headers.Contains("Pragma").Should().BeFalse();
    }

    [Fact]
    public async Task The_capability_badge_stays_private_and_carries_no_cookie()
    {
        using var client = Client();

        var response = await client.GetAsync("/badge/github/no-such-owner/no-such-repo.svg?token=not-a-real-token");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cacheControl = response.Headers.CacheControl!;
        cacheControl.Private.Should().BeTrue();
        cacheControl.Public.Should().BeFalse();
        cacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(300));
        SetCookies(response).Should().BeEmpty();
    }

    [Fact]
    public async Task A_non_badge_response_still_carries_the_xsrf_cookie()
    {
        using var client = Client();

        var response = await client.GetAsync("/spark");

        SetCookies(response).Should().Contain(c => c.StartsWith("XSRF-TOKEN="),
            "the mint is active in this host; without this, the badge assertions above prove nothing");
    }
}
