using System.Net;
using Microsoft.Playwright;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Ssr;

/// <summary>
/// Server rendering end to end on mintplayer.com (<c>docs/spark_ssr_PRD.md</c> M5): the page a crawler gets holds the
/// object's data and SEO, a stale slug and a missing object answer as a crawler needs, and the browser hydrates
/// that page without asking the server again. The in-process half (a supplied response equals the HTTP one) is
/// <c>SparkPrerenderEquivalenceTests</c>.
/// </summary>
[Collection(MintPlayerE2ECollection.Name)]
public class ServerRenderingTests
{
    private const int Timeout = 30_000;

    private static readonly string SongPath = $"/po/song/{Uri.EscapeDataString(MintPlayerTestHost.SongId)}/{MintPlayerTestHost.SongSlug}";

    private readonly MintPlayerE2ECollectionFixture _fixture;
    public ServerRenderingTests(MintPlayerE2ECollectionFixture fixture) => _fixture = fixture;

    private HttpClient NewHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })
    { BaseAddress = new Uri(_fixture.Host.AppUrl) };

    [Fact]
    public async Task A_song_page_is_served_with_its_data_and_its_seo()
    {
        using var client = NewHttpClient();

        using var response = await client.GetAsync(SongPath);
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain(">A Sky Full of Stars - MintPlayer</title>", "the title is written on the server (D12). Host log:\n{0}", _fixture.Host.RecentLog(120));
        html.Should().Contain("Coldplay", "the artists' breadcrumbs are rendered, not left for the browser");
        html.Should().Contain("Avicii");
        html.Should().Contain($"rel=\"canonical\"", "the canonical link is written on the server");
        html.Should().Contain(MintPlayerTestHost.SongSlug);
        html.Should().Contain("application/ld+json");
        html.Should().Contain("\"byArtist\"", "MintPlayer's SEO hook adds the song's artists to the JSON-LD");
        html.Should().Contain("ng-state", "the page's responses travel to the browser in the transfer state");
    }

    [Fact]
    public async Task A_stale_slug_is_a_301_to_the_canonical_one()
    {
        using var client = NewHttpClient();

        using var response = await client.GetAsync($"/po/song/{Uri.EscapeDataString(MintPlayerTestHost.SongId)}/wrong-slug");

        response.StatusCode.Should().Be(HttpStatusCode.MovedPermanently);
        response.Headers.Location!.OriginalString.Should().EndWith($"/{MintPlayerTestHost.SongSlug}");
    }

    [Fact]
    public async Task A_missing_song_is_not_a_200_and_is_not_indexed()
    {
        using var client = NewHttpClient();

        using var response = await client.GetAsync($"/po/song/{Uri.EscapeDataString("Songs/e2e-missing")}");

        // Anonymous: missing and denied answer alike, as the API does (a uniform 401).
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.TryGetValues("X-Robots-Tag", out var robots).Should().BeTrue();
        robots!.Should().Contain("noindex");
    }

    /// <summary>The front page, which Angular redirects to the home board (whose paths name "/" for it), and a song page.</summary>
    public static TheoryData<string, string> Pages => new()
    {
        { "/", "A Sky Full of Stars" },
        { SongPath, "Coldplay" },
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task The_browser_hydrates_a_page_without_asking_the_server_again(string path, string shows)
    {
        // No service worker: Chromium refuses to register one from an origin whose certificate it does not trust
        // (the CI runner does not trust the dev certificate; IgnoreHTTPSErrors does not cover it), and Angular logs
        // NG05604. Hydration does not depend on it.
        var context = await _fixture.Browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ServiceWorkers = ServiceWorkerPolicy.Block });
        try
        {
            var page = await context.NewPageAsync();
            var sparkRequests = new List<string>();
            var problems = new List<string>();
            page.Request += (_, request) =>
            {
                if (new Uri(request.Url).AbsolutePath.StartsWith("/spark/", StringComparison.Ordinal))
                    lock (sparkRequests) sparkRequests.Add($"{request.Method} {request.Url}");
            };
            page.Console += (_, message) =>
            {
                if (message.Text == "Service Worker registration blocked by Playwright")
                    return;
                if (message.Type is "error" or "warning" || message.Text.Contains("NG0", StringComparison.Ordinal))
                    lock (problems) problems.Add($"{message.Type}: {message.Text}");
            };

            await page.GotoAsync(_fixture.Host.AppUrl + path, new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = Timeout });
            await Microsoft.Playwright.Assertions.Expect(page.GetByText(shows).First).ToBeVisibleAsync(new() { Timeout = Timeout });

            lock (sparkRequests)
                sparkRequests.Should().BeEmpty("on {0} every request the page makes was answered on the server and travels in the transfer state (I22)", path);
            lock (problems)
                problems.Should().BeEmpty("hydration must not warn (I23)");
        }
        finally
        {
            await context.CloseAsync();
        }
    }
}
