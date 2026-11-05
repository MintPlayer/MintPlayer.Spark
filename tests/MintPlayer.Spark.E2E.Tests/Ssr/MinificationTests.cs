using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Ssr;

/// <summary>
/// HTML minification on mintplayer.com (<c>docs/html_minification_PRD.md</c> M2; SP2 and SP3 run here, against the real
/// pipeline). <see cref="ServerRenderingTests"/> runs with minification on as well, so its hydration test is the NG0500
/// check on the minified pages. The host serves the home board twice: at <c>/</c> minified, and at
/// <see cref="MintPlayerTestHost.UnminifiedHome"/> as rendered, which is the baseline here.
/// </summary>
[Collection(MintPlayerE2ECollection.Name)]
public partial class MinificationTests
{
    private const int Timeout = 30_000;

    private static readonly string SongPath = $"/po/song/{Uri.EscapeDataString(MintPlayerTestHost.SongId)}/{MintPlayerTestHost.SongSlug}";

    private readonly MintPlayerE2ECollectionFixture _fixture;
    public MinificationTests(MintPlayerE2ECollectionFixture fixture) => _fixture = fixture;

    private HttpClient NewHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })
    { BaseAddress = new Uri(_fixture.Host.AppUrl) };

    [Fact]
    public async Task A_rendered_page_is_minified_and_keeps_its_hydration_markup()
    {
        using var client = NewHttpClient();

        var minified = await client.GetStringAsync("/");
        var rendered = await client.GetStringAsync(MintPlayerTestHost.UnminifiedHome);

        BetweenTags().Count(minified).Should().Be(0, "no line breaks are left between elements. Host log:\n{0}", _fixture.Host.RecentLog(60));
        BetweenTags().Count(rendered).Should().BeGreaterThan(0, "the baseline is the page as rendered, or this test compares nothing");
        minified.Length.Should().BeLessThan(rendered.Length);
        minified.Should().Contain("ngh=\"", "the hydration attributes stay");
        minified.Should().Contain("<script id=\"ng-state\" type=\"application/json\">", "and the transfer state");
        CommentCount(minified).Should().Be(CommentCount(rendered), "RemoveComments is off: every hydration marker stays (NG0500)");
    }

    [Fact]
    public async Task The_minified_page_looks_the_same_as_the_rendered_one()
    {
        // The CSS minifier is where the saving is (SP1), and the one setting that can change what the page looks like.
        // Every element's computed style, after hydration, minified against as rendered.
        var minified = await StyleFingerprint("/");
        var rendered = await StyleFingerprint(MintPlayerTestHost.UnminifiedHome);

        rendered.Length.Should().BeGreaterThan(100, "the page must have rendered, or this test compares nothing");
        var differences = minified.Zip(rendered).Select((pair, i) => (pair, i)).Where(x => x.pair.First != x.pair.Second)
            .Select(x => $"#{x.i}: {x.pair.Second} => {x.pair.First}").Take(5).ToList();
        differences.Should().BeEmpty();
        minified.Length.Should().Be(rendered.Length);
    }

    [Fact]
    public async Task The_page_is_still_compressed_and_keeps_its_headers()
    {
        using var client = NewHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, SongPath);
        request.Headers.AcceptEncoding.ParseAdd("br");

        using var response = await client.SendAsync(request);
        await using var brotli = new BrotliStream(await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        using var reader = new StreamReader(brotli, Encoding.UTF8);
        var html = await reader.ReadToEndAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.Should().Equal(["br"], "UseResponseCompression is outside the minification (D2)");
        html.Should().Contain(">A Sky Full of Stars - MintPlayer</title>");
        BetweenTags().Count(html).Should().Be(0);
        response.Headers.CacheControl!.NoStore.Should().BeTrue("the render's own headers survive the minification");
        response.Headers.Vary.Should().Contain("Cookie");
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.Contains("X-HTML-Minification-Powered-By").Should().BeFalse();
    }

    [Fact]
    public async Task The_service_worker_s_template_is_byte_identical_to_the_build()
    {
        // ngsw.json holds the hash of /index.csr.html; a minified copy would put the service worker into its degraded mode.
        using var client = NewHttpClient();

        var served = await client.GetByteArrayAsync("/index.csr.html");

        var built = await File.ReadAllBytesAsync(Path.Combine(RepoRoot(), "apps", "MintPlayer", "MintPlayer", "ClientApp", "dist", "ClientApp", "browser", "index.csr.html"));
        served.Should().Equal(built);
    }

    [Fact]
    public async Task A_spark_json_response_is_untouched()
    {
        // /spark is mapped before the SPA branch, so the minifier (inside that branch) never sees it.
        using var client = NewHttpClient();

        using var response = await client.GetAsync("/spark/culture");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        body.TrimStart().Should().StartWith("{");
    }

    private async Task<string[]> StyleFingerprint(string path)
    {
        var context = await _fixture.Browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ServiceWorkers = ServiceWorkerPolicy.Block, ViewportSize = new() { Width = 1280, Height = 900 } });
        try
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync(_fixture.Host.AppUrl + path, new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = Timeout });
            await Microsoft.Playwright.Assertions.Expect(page.GetByText("A Sky Full of Stars").First).ToBeVisibleAsync(new() { Timeout = Timeout });
            return await page.EvaluateAsync<string[]>("""
                () => {
                  const props = ['display','position','color','background-color','font-size','font-weight','font-family','margin-top','margin-left','padding-top','padding-left','width','height','border-top-width','border-top-color','flex-direction','gap','visibility','opacity'];
                  return [...document.body.querySelectorAll('*')].map(e => { const cs = getComputedStyle(e); return e.tagName + '|' + props.map(k => cs.getPropertyValue(k)).join(','); });
                }
                """);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private static int CommentCount(string html) => Regex.Count(html, "<!--");

    /// <summary>A line break between two tags: what indentation leaves behind.</summary>
    [GeneratedRegex(@">\s*\n\s*<")]
    private static partial Regex BetweenTags();

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MintPlayer.Spark.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not locate MintPlayer.Spark.slnx from " + AppContext.BaseDirectory);
    }
}
