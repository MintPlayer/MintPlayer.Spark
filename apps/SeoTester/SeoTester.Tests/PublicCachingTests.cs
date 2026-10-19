using System.Net;
using SeoTester.Tests._Infrastructure;

namespace SeoTester.Tests;

/// <summary>
/// FR1 (docs/reference_data_PRD.md §6): SeoTester's public pages say <c>Cache-Control: public</c>, but the XSRF cookie
/// generator puts a per-visitor <c>Set-Cookie</c> on every response that does not opt out, and a response carrying one has
/// its Cache-Control forced to <c>private</c> (SparkMiddleware, UseAntiforgeryGenerator). So nothing shared ever cached
/// them. Red before <c>.SkipXsrfToken()</c> on each endpoint; the badge is covered in
/// <c>ReportSurfaceTests.An_Admin_makes_a_badge_that_lives_and_dies_with_the_share_link</c>.
/// </summary>
[Collection(SeoTesterHostCollection.Name)]
public class PublicCachingTests(SeoTesterHostFixture host)
{
    [Theory]
    [InlineData("/privacy")]
    [InlineData("/terms")]
    [InlineData("/bot")]
    [InlineData("/seotester/catalogue?culture=en")]
    public async Task A_public_page_stays_shared_cacheable_and_sets_no_cookie(string url)
    {
        using var client = host.CreateClient();
        using var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Set-Cookie").Should().BeFalse("a per-visitor cookie makes the response private");
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Public.Should().BeTrue();
        response.Headers.CacheControl.Private.Should().BeFalse();
    }
}
