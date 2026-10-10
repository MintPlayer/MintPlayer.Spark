using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.PageSpeed;
using SeoTester.Reports;
using SeoTester.Settings;
using SeoTester.Tests._Infrastructure;
using Xunit.Abstractions;
using static SeoTester.Tests._Infrastructure.SeoApi;

namespace SeoTester.Tests.Rendering;

/// <summary>
/// M10 end to end (<c>docs/seo_analyzer_PRD.md</c> §4.3, F12–F14): StartAudit → probe → crawl → render (a local
/// Chromium, every request routed through the fake web) and PageSpeed Insights (the recorded real answers of spike S5)
/// side by side → aggregate, then the PDF. Its own host (browser on, a fake PSI key). Needs Playwright's Chromium.
/// </summary>
[Collection(RenderHostCollection.Name)]
public class RenderPipelineTests(RenderHostFixture host, ITestOutputHelper output)
{
    private async Task<string> StartAsync(Site site, string user = Max)
    {
        await SetSettingAsync(host, SeoQuotas.Audits.Limit.Key, "-1");
        var (status, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "StartAudit", site.Id!, user);
        status.Should().Be(HttpStatusCode.OK, raw);
        return body.GetProperty("result").GetProperty("runId").GetString()!;
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Fixtures", name));

    private void PsiAnswers(string origin, string fixture, HttpStatusCode status = HttpStatusCode.OK)
    {
        var options = host.Factory.Services.GetRequiredService<IOptions<PageSpeedOptions>>().Value;
        host.Web.Pages[PageSpeedClient.RequestUrl(options, new Uri(origin + "/")).AbsoluteUri] =
            new FakeResponse(status, Fixture(fixture), ContentType: fixture.EndsWith(".html") ? "text/html" : "application/json");
    }

    /// <summary>
    /// The home page: an SPA link, GA4 sending a hit on load (no consent manager), a service worker, an invalid ARIA role,
    /// low contrast, tiny touch targets, a skipped heading level, no main landmark, a block wider than a phone. /csr renders
    /// all its text with JavaScript. /good is plain and accessible.
    /// </summary>
    private string BuildSite()
    {
        var h = FakeSite.NewHost();
        var o = "https://" + h;
        host.Web.Page(o + "/robots.txt", "User-agent: *\nAllow: /\n");
        host.Web.Html(o + "/", $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Home of the rendered example shop</title><meta name="description" content="A description long enough for search results, between seventy and one hundred sixty characters.">
            <script async src="https://www.googletagmanager.com/gtag/js?id=G-TEST1"></script></head>
            <body><h1>Rendered example shop</h1>
            <nav><a href="/app/b" data-spa>Go to b</a> <a href="/csr">Client page</a> <a href="/good">Good page</a></nav>
            <h3>Skipped a level</h3>
            <p style="color:#bbb;background:#fff">{{string.Join(' ', Enumerable.Range(0, 120).Select(i => "word" + i))}}</p>
            <div role="buttonx">Not a real role</div>
            <button style="width:8px;height:8px;padding:0;border:0">a</button><button style="width:8px;height:8px;padding:0;border:0">b</button>
            <div style="width:900px">Wide block</div>
            <script>
              document.addEventListener('click', e => { const a = e.target.closest('a[data-spa]'); if (a) { e.preventDefault(); history.pushState({}, '', a.href); } });
              navigator.serviceWorker && navigator.serviceWorker.register('/sw.js');
            </script></body></html>
            """);
        host.Web.Pages["https://www.googletagmanager.com/gtag/js?id=G-TEST1"] = new FakeResponse(HttpStatusCode.OK,
            "fetch('https://www.google-analytics.com/g/collect?v=2&tid=G-TEST1&en=page_view', { method: 'POST', mode: 'no-cors' });",
            ContentType: "application/javascript");
        host.Web.Pages[o + "/sw.js"] = new FakeResponse(HttpStatusCode.OK, "self.addEventListener('fetch', e => {});", ContentType: "application/javascript",
            Headers: new Dictionary<string, string> { ["Cache-Control"] = "max-age=3600" });
        host.Web.Html(o + "/csr", """
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Client rendered page of the shop</title></head><body><div id="app"></div><script src="/app.js"></script></body></html>
            """);
        host.Web.Pages[o + "/app.js"] = new FakeResponse(HttpStatusCode.OK,
            "document.getElementById('app').innerHTML = '<main><h1>Rendered by script</h1><p>' + Array.from({length: 300}, (_, i) => 'text' + i).join(' ') + '</p></main>';",
            ContentType: "application/javascript");
        host.Web.Html(o + "/good", FakeSite.Page("A plain and accessible page", "/"));
        FakeSite.Registries(host.Web);
        return h;
    }

    [Fact]
    public async Task An_audit_renders_its_sample_measures_PageSpeed_and_prints_a_PDF()
    {
        var site = await SeedSiteAsync(host, Acme, hostName: BuildSite());
        PsiAnswers(site.Origin, "psi_mintplayer_mobile_perf.json");
        var runId = await StartAsync(site);

        var run = await FakeSite.FinishedAsync(host.Database, runId, TimeSpan.FromMinutes(3));

        run.Status.Should().Be(AuditRunStatus.Completed, run.Error);
        var results = run.CheckResults.ToDictionary(r => r.CheckId);
        foreach (var r in run.CheckResults.Where(r => RenderedOrPsi.Contains(r.CheckId)))
            output.WriteLine($"{r.CheckId}: {r.Status} {r.Affected} {string.Join(",", r.SampleUrls)} {r.Note}");
        var o = site.Origin;
        results["technical.raw-vs-rendered"].Status.Should().Be("Fail");
        results["technical.raw-vs-rendered"].SampleUrls.Should().Equal(o + "/csr");
        results["mobile.responsive"].SampleUrls.Should().Equal(o + "/");
        results["accessibility.aria"].SampleUrls.Should().Contain(o + "/");
        results["accessibility.color-contrast"].SampleUrls.Should().Contain(o + "/");
        results["accessibility.touch-targets"].SampleUrls.Should().Contain(o + "/");
        results["accessibility.heading-order"].SampleUrls.Should().Contain(o + "/");
        results["accessibility.landmarks"].SampleUrls.Should().Contain(o + "/");
        results["accessibility.color-contrast"].SampleUrls.Should().NotContain(o + "/good");
        results["technical.mobile-parity"].Status.Should().Be("Pass");
        results["performance.page-weight"].Status.Should().Be("Pass");
        results["mobile.service-worker"].Status.Should().Be("Pass", "/sw.js handles fetch");
        results["mobile.service-worker-caching"].Status.Should().Be("Warn", "sw.js is cached an hour");
        results["security.analytics-consent"].Status.Should().Be("Warn", "GA4 sent a hit on load with no consent manager");
        results["technical.spa-analytics"].Status.Should().Be("Fail", "the SPA route change sent no hit");
        // PageSpeed: the recorded mintplayer.com answer (Lighthouse 13.5.0, score 46, no CrUX data).
        results["performance.lab-score"].Status.Should().Be("Fail");
        results["performance.lab-score"].SampleUrls.Should().Equal(o + "/");
        results["performance.core-web-vitals"].Status.Should().Be("NotApplicable");
        results["performance.core-web-vitals"].Note.Should().Be("the Chrome UX Report has no field data for this page or its origin");

        // Every browser request went through the router: the analytics hit was answered locally, never sent on.
        host.Web.Requests.Should().NotContain(r => r.Url.Contains("google-analytics.com/g/collect"));
        host.Web.Requests.Should().Contain(r => r.Url == o + "/app.js");
        // The PageSpeed key travelled in a header, never in a URL.
        var psi = host.Web.Requests.Where(r => r.Url.StartsWith("https://www.googleapis.com/pagespeedonline/", StringComparison.Ordinal)).ToList();
        psi.Should().HaveCount(1);
        psi[0].Url.Should().NotContain(RenderHostFixture.FakePsiKey);
        psi[0].HeaderNames.Should().Contain("X-Goog-Api-Key");

        // F14: the PDF, for members of the organization only.
        var (status, body, raw) = await ActionAsync(host, AuditRunTypeId, "AuditRun", "ExportReport", runId, Vic);
        status.Should().Be(HttpStatusCode.OK, raw);
        var url = body.GetProperty("result").GetProperty("url").GetString()!;
        var report = await WaitForReportAsync(RunReport.IdFor(runId, "en"));
        output.WriteLine($"PDF: {report.Bytes} bytes in {report.RenderMs} ms");
        report.Bytes.Should().BeGreaterThan(1000);
        var client = host.CreateClient();
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            SeoRequests.AddIdentity(request, Vic, []);
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
            var bytes = await response.Content.ReadAsByteArrayAsync();
            System.Text.Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
        }
        foreach (var outsider in new[] { Bob, null })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            SeoRequests.AddIdentity(request, outsider, []);
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, "only the run's organization reads its report");
        }
    }

    [Fact]
    public async Task A_PageSpeed_quota_answer_is_asked_again_later_then_recorded_not_measured()
    {
        var site = await SeedSiteAsync(host, Acme, hostName: BuildSite());
        PsiAnswers(site.Origin, "psi_429_quota_zero.json", HttpStatusCode.TooManyRequests);
        var runId = await StartAsync(site);

        var run = await FakeSite.FinishedAsync(host.Database, runId, TimeSpan.FromMinutes(3));

        run.Status.Should().Be(AuditRunStatus.Completed, run.Error);
        var results = run.CheckResults.ToDictionary(r => r.CheckId);
        results["performance.lab-score"].Status.Should().Be("NotApplicable");
        results["performance.lab-score"].Note.Should().Be("the PageSpeed Insights quota is exhausted");
        host.Web.Requests.Count(r => r.Url.StartsWith("https://www.googleapis.com/pagespeedonline/", StringComparison.Ordinal) && r.Url.Contains(Uri.EscapeDataString(site.Origin)))
            .Should().Be(2, "asked once, then once more after the quota wait (QuotaRetries 1)");
    }

    /// <summary>
    /// Spike S3's numbers on this machine (a launched Chromium; the container's are measured at the M12 deploy): each step
    /// of a render, the memory of Chromium's processes, and the facts the checks read, straight from PageRenderer.
    /// </summary>
    [Fact]
    public async Task Rendering_a_page_measures_layout_coverage_axe_and_the_served_HTML()
    {
        var h = BuildSite();
        var origin = new Uri("https://" + h + "/");
        var services = host.Factory.Services;
        var renderer = services.GetRequiredService<SeoTester.Rendering.PageRenderer>();
        using var pacer = new SeoTester.Crawler.HostPacer(1, TimeSpan.Zero, null, TimeProvider.System);
        using var network = new SeoTester.Rendering.RenderNetwork(services.GetRequiredService<IHttpClientFactory>(), origin,
            SeoTester.Crawling.RobotsTxt.AllowAll, pacer, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        for (var round = 0; round < 3; round++)
        {
            foreach (var path in new[] { "/", "/csr", "/good" })
            {
                var render = await renderer.RenderAsync(network, new Uri(origin, path), probeRoute: path == "/", CancellationToken.None);
                var memory = System.Diagnostics.Process.GetProcessesByName("chrome-headless-shell")
                    .Sum(p => { try { return p.WorkingSet64; } catch (InvalidOperationException) { return 0; } }) / (1024 * 1024);
                output.WriteLine($"round {round} {path}: {string.Join(", ", render.Timings.Select(t => $"{t.Key} {t.Value} ms"))}; requests {render.Facts.Network.Count}; Chromium processes {memory} MB");
                render.Facts.Error.Should().BeNull();
                if (path == "/csr")
                {
                    render.Facts.Raw!.Words.Should().BeLessThan(20);
                    render.Facts.Desktop!.Words.Should().BeGreaterThan(300);
                    render.Facts.Coverage!.Bytes.Should().BeGreaterThan(0);
                }
                if (path == "/")
                {
                    render.Facts.Mobile!.ScrollWidth.Should().BeGreaterThan(375);
                    var rules = render.Facts.Axe!.Select(v => v.Id).ToList();
                    foreach (var rule in new[] { "color-contrast", "aria-roles", "target-size", "heading-order" })
                        rules.Should().Contain(rule);
                    render.ServiceWorkerUrl.Should().Be(origin + "sw.js");
                }
            }
        }
        output.WriteLine($"network fetches {network.Fetches} (static resources fetched once per run), analytics hits answered locally {network.Hits}");
    }

    private static readonly HashSet<string> RenderedOrPsi = [.. SeoTester.Checks.RenderChecks.Ids, .. SeoTester.PageSpeed.PsiChecks.Ids];

    private async Task<RunReport> WaitForReportAsync(string id)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (await LoadDocumentAsync<RunReport>(host, id) is { } report)
                return report;
            await Task.Delay(200);
        }
        throw new TimeoutException($"No report {id} after 60 s.");
    }
}

public sealed class RenderHostFixture : SeoTesterHostFixture
{
    /// <summary>Not a key: the fake web answers PageSpeed Insights from recorded fixtures.</summary>
    public const string FakePsiKey = "fake-psi-key-for-tests";

    protected override IReadOnlyDictionary<string, string?>? HostSettings => new Dictionary<string, string?>
    {
        ["SeoTester:Limits:Unverified:RequestInterval"] = "00:00:00.020",
        ["SeoTester:Crawl:BatchSize"] = "4",
        ["SeoTester:Browser:Mode"] = "Launch",
        ["SeoTester:Browser:SettleTimeout"] = "00:00:02",
        ["SeoTester:PageSpeed:ApiKey"] = FakePsiKey,
        ["SeoTester:PageSpeed:QuotaRetries"] = "1",
        ["SeoTester:PageSpeed:QuotaRetryDelay"] = "00:00:01",
    };
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RenderHostCollection : ICollectionFixture<RenderHostFixture>
{
    public const string Name = "SeoTester render host";
}
