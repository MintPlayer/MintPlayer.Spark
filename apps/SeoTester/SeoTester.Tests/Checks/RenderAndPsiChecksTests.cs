using SeoTester.Checks;
using SeoTester.Entities;
using SeoTester.PageSpeed;
using SeoTester.Rendering;
using SeoTester.Reports;
using SeoTester.Scoring;

namespace SeoTester.Tests.Checks;

/// <summary>
/// M10's pure parts: the PageSpeed Insights parser on the real answers recorded in spike S5 (Lighthouse 13.5.0;
/// example.com with CrUX field data, mintplayer.com without; the 429 bodies of the keyless calls), the rendered
/// checks' thresholds, V8 coverage arithmetic, the render sample and the printed report.
/// </summary>
public class RenderAndPsiChecksTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Fixtures", name));

    private static CheckFinding One(IEnumerable<CheckFinding> findings, string id) => findings.Single(f => f.CheckId == id);

    // ---- PageSpeed Insights (S5) ------------------------------------------------------------------------------------

    [Fact]
    public void A_real_answer_with_field_data_passes_both_checks()
    {
        var outcome = PsiReport.Parse(200, Fixture("psi_example_mobile_all.json"), "https://example.com/");

        outcome.Kind.Should().Be(PsiOutcomeKind.Measured);
        var summary = outcome.Summary!;
        summary.LighthouseVersion.Should().Be("13.5.0");
        summary.FormFactor.Should().Be("mobile");
        summary.PerformanceScore.Should().Be(100);
        summary.FieldIsOrigin.Should().BeFalse();
        summary.Field.Single(f => f.Name == "LARGEST_CONTENTFUL_PAINT_MS").Percentile.Should().Be(1038);
        summary.LabMetrics.Keys.Should().Contain("largest-contentful-paint");
        summary.Opportunities.Select(o => o.Id).Should().Contain("network-dependency-tree-insight");

        var findings = PsiChecks.Evaluate(summary);
        One(findings, PsiChecks.LabScore).Status.Should().Be(CheckStatus.Pass);
        One(findings, PsiChecks.CoreWebVitals).Status.Should().Be(CheckStatus.Pass, "every CrUX metric of example.com is FAST");
    }

    [Fact]
    public void A_real_answer_without_field_data_fails_the_lab_score_and_leaves_the_vitals_not_measured()
    {
        var summary = PsiReport.Parse(200, Fixture("psi_mintplayer_mobile_perf.json"), "https://mintplayer.com/").Summary!;

        summary.PerformanceScore.Should().Be(46);
        summary.Field.Should().BeEmpty("CrUX has no data for a small site: only initial_url comes back");
        summary.Opportunities.Should().HaveCount(5);

        var findings = PsiChecks.Evaluate(summary);
        var lab = One(findings, PsiChecks.LabScore);
        lab.Status.Should().Be(CheckStatus.Fail);
        lab.Detail!["n"].Should().Be("46");
        lab.Detail["value"].Should().NotBeNullOrEmpty();
        var vitals = One(findings, PsiChecks.CoreWebVitals);
        vitals.Status.Should().Be(CheckStatus.NotApplicable);
        vitals.Detail!["reason"].Should().Contain("no field data");
    }

    [Fact]
    public void The_desktop_answer_is_read_as_desktop()
        => PsiReport.Parse(200, Fixture("psi_mintplayer_desktop_perf.json"), "https://mintplayer.com/").Summary!.FormFactor.Should().Be("desktop");

    [Fact]
    public void Slow_field_data_fails_on_the_worst_metric()
    {
        var summary = new PsiSummary("https://a.test/", "mobile", "13.5.0", 95, new Dictionary<string, double>(), [],
            [new FieldMetric("LARGEST_CONTENTFUL_PAINT_MS", 2100, "FAST"), new FieldMetric("INTERACTION_TO_NEXT_PAINT", 640, "SLOW"), new FieldMetric("CUMULATIVE_LAYOUT_SHIFT_SCORE", 15, "AVERAGE")], true);

        var vitals = One(PsiChecks.Evaluate(summary), PsiChecks.CoreWebVitals);

        vitals.Status.Should().Be(CheckStatus.Fail);
        vitals.Detail!["value"].Should().Be("Interaction to Next Paint");
        vitals.Detail["n"].Should().Be("640 ms");
        vitals.Detail["limit"].Should().Be("200 ms");
        vitals.Detail["origin"].Should().Be("true");
    }

    [Theory]
    [InlineData("psi_429_quota_zero.json")]   // the keyless call: quota_limit_value "0"
    [InlineData("psi_429_sorry.html")]        // Google's "unusual traffic" HTML page
    public void A_429_is_the_quota_never_the_site(string fixture)
        => PsiReport.Parse(429, Fixture(fixture), "https://example.com/").Kind.Should().Be(PsiOutcomeKind.QuotaExhausted);

    [Fact]
    public void Another_error_keeps_the_API_message()
    {
        const string body = """{"error":{"code":500,"message":"Lighthouse returned error: FAILED_DOCUMENT_REQUEST.","status":"INTERNAL"}}""";

        var outcome = PsiReport.Parse(500, body, "https://example.com/");

        outcome.Kind.Should().Be(PsiOutcomeKind.Failed);
        outcome.Reason.Should().Be("Lighthouse returned error: FAILED_DOCUMENT_REQUEST.");
        PsiReport.Parse(200, "<html>", "https://example.com/").Kind.Should().Be(PsiOutcomeKind.Failed);
    }

    [Fact]
    public void The_recorded_fixtures_hold_no_key()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Data", "Fixtures"), "psi_*"))
        {
            var text = File.ReadAllText(file);
            text.Should().NotContain("key=");
            text.Should().NotContain("AIza", "a Google API key starts with AIza");
        }
    }

    // ---- Rendered checks ---------------------------------------------------------------------------------------------

    private static RenderedPageFacts Facts(RenderedView? mobile = null, RenderedView? desktop = null, RawView? raw = null, IReadOnlyList<AxeViolation>? axe = null,
        IReadOnlyList<NetworkEntry>? network = null, ScriptCoverage? coverage = null)
    {
        var view = new RenderedView { ViewportWidth = 375, ScrollWidth = 375, Words = 300, H1Count = 1, Title = "T", TextCharacters = 2000, Links = ["https://a.test/x"] };
        return new RenderedPageFacts
        {
            Url = "https://a.test/",
            Mobile = mobile ?? view,
            Desktop = desktop ?? view with { ViewportWidth = 1350, ScrollWidth = 1350 },
            Raw = raw ?? new RawView("T", 1, 290, 1),
            Axe = axe ?? [],
            Network = network ?? [],
            Coverage = coverage,
        };
    }

    [Fact]
    public void A_clean_render_passes_every_page_check()
    {
        var findings = RenderChecks.Evaluate(Facts(coverage: new ScriptCoverage(50_000, 10_000)));

        findings.Select(f => f.CheckId).Should().BeEquivalentTo(RenderChecks.PageIds);
        findings.Where(f => f.CheckId != RenderChecks.LazyLoading).Should().OnlyContain(f => f.Status == CheckStatus.Pass);
        One(findings, RenderChecks.LazyLoading).Status.Should().Be(CheckStatus.NotApplicable, "a page without images");
    }

    [Fact]
    public void A_client_rendered_page_fails_raw_vs_rendered()
    {
        One(RenderChecks.Evaluate(Facts(raw: new RawView("T", 0, 5, 0))), RenderChecks.RawVsRendered).Status.Should().Be(CheckStatus.Fail);
        One(RenderChecks.Evaluate(Facts(raw: new RawView("T", 1, 150, 1))), RenderChecks.RawVsRendered).Status.Should().Be(CheckStatus.Warn, "half the words");
    }

    [Fact]
    public void Overflow_and_small_text_fail_the_phone_layout()
    {
        One(RenderChecks.Evaluate(Facts(mobile: new RenderedView { ViewportWidth = 375, ScrollWidth = 900, Words = 300 })), RenderChecks.Responsive).Detail!["value"].Should().Be("900");
        One(RenderChecks.Evaluate(Facts(mobile: new RenderedView { ViewportWidth = 375, ScrollWidth = 375, Words = 300, TextCharacters = 1000, SmallTextCharacters = 600 })), RenderChecks.Responsive)
            .Status.Should().Be(CheckStatus.Fail);
    }

    [Fact]
    public void A_lazy_LCP_image_fails_and_eager_images_below_the_fold_warn()
    {
        var images = Enumerable.Range(0, 4).Select(i => new ImageFact($"/{i}.jpg", false, false)).ToList();
        One(RenderChecks.Evaluate(Facts(mobile: new RenderedView { ViewportWidth = 375, ScrollWidth = 375, Words = 300, Images = images, LcpTag = "IMG", LcpLazy = true })), RenderChecks.LazyLoading)
            .Status.Should().Be(CheckStatus.Fail);
        One(RenderChecks.Evaluate(Facts(mobile: new RenderedView { ViewportWidth = 375, ScrollWidth = 375, Words = 300, Images = images })), RenderChecks.LazyLoading)
            .Status.Should().Be(CheckStatus.Warn);
    }

    [Fact]
    public void Axe_rules_map_onto_their_checks_by_impact()
    {
        var findings = RenderChecks.Evaluate(Facts(axe:
        [
            new AxeViolation("aria-roles", "critical", 2, "ARIA roles used must conform to valid values"),
            new AxeViolation("heading-order", "moderate", 1, null),
            new AxeViolation("region", "moderate", 3, null),
            new AxeViolation("image-alt", "critical", 9, null),     // measured by accessibility.img-alt already
            new AxeViolation("list", "serious", 1, "lists"),
        ]));

        One(findings, RenderChecks.Aria).Status.Should().Be(CheckStatus.Fail);
        One(findings, RenderChecks.Aria).Detail!["n"].Should().Be("2");
        One(findings, RenderChecks.HeadingOrder).Status.Should().Be(CheckStatus.Warn);
        One(findings, RenderChecks.Landmarks).Status.Should().Be(CheckStatus.Warn);
        One(findings, RenderChecks.AxeOther).Detail!["rule"].Should().Be("list", "image-alt is not counted twice");
        One(findings, RenderChecks.ColorContrast).Status.Should().Be(CheckStatus.Pass);
    }

    [Fact]
    public void A_heavy_page_and_unused_script_are_flagged()
    {
        var network = Enumerable.Range(0, 160).Select(i => new NetworkEntry($"https://a.test/{i}", "image", 200, 10_000, false, "fulfilled")).ToList();
        One(RenderChecks.Evaluate(Facts(network: network)), RenderChecks.PageWeight).Status.Should().Be(CheckStatus.Fail);
        One(RenderChecks.Evaluate(Facts(coverage: new ScriptCoverage(300 * 1024, 200 * 1024))), RenderChecks.CodeSplitting).Status.Should().Be(CheckStatus.Warn);
    }

    [Fact]
    public void A_page_that_could_not_be_rendered_is_an_error_not_a_failure()
        => RenderChecks.Evaluate(new RenderedPageFacts { Url = "https://a.test/", Error = "timed out" }).Should().OnlyContain(f => f.Status == CheckStatus.Error);

    [Fact]
    public void Site_level_render_findings()
    {
        var worker = new ServiceWorkerFact("https://a.test/sw.js", 200, "max-age=86400", true);
        var site = RenderChecks.EvaluateSite(worker, new AnalyticsFact(["Google Analytics"], 1, false, true, 0), pwa: false);
        One(site, RenderChecks.ServiceWorker).Status.Should().Be(CheckStatus.Pass);
        One(site, RenderChecks.ServiceWorkerCaching).Detail!["value"].Should().Be("86400");
        One(site, RenderChecks.AnalyticsConsent).Status.Should().Be(CheckStatus.Warn);
        One(site, RenderChecks.SpaAnalytics).Status.Should().Be(CheckStatus.Fail);

        var none = RenderChecks.EvaluateSite(null, null, pwa: true);
        One(none, RenderChecks.ServiceWorker).Status.Should().Be(CheckStatus.Fail, "a PWA profile needs a service worker");
        One(RenderChecks.EvaluateSite(null, null, pwa: false), RenderChecks.ServiceWorker).Status.Should().Be(CheckStatus.NotApplicable);
        One(RenderChecks.EvaluateSite(worker with { CacheControl = "no-cache" }, null, false), RenderChecks.ServiceWorkerCaching).Status.Should().Be(CheckStatus.Pass);
        One(RenderChecks.EvaluateSite(null, new AnalyticsFact(["Plausible"], 0, true, false, 0), false), RenderChecks.AnalyticsConsent).Status.Should().Be(CheckStatus.Pass);
    }

    [Theory]
    [InlineData("https://www.google-analytics.com/g/collect?v=2", "Google Analytics")]
    [InlineData("https://region1.google-analytics.com/g/collect?v=2", "Google Analytics")]
    [InlineData("https://www.facebook.com/tr?id=1", "Meta Pixel")]
    [InlineData("https://stats.example.com/matomo.php?idsite=1", "Matomo")]
    [InlineData("https://www.google-analytics.com/analytics.js", null)]
    [InlineData("https://example.com/g/collect", null)]
    public void Analytics_hits_are_recognised(string url, string? tool) => AnalyticsVendors.ToolForHit(new Uri(url)).Should().Be(tool);

    // ---- Coverage, sample, report ------------------------------------------------------------------------------------

    [Fact]
    public void Coverage_takes_the_innermost_count()
    {
        // A 100-character script whose top level ran, with a function (20..60) that never ran and, inside it, a block (30..40) that did.
        var (length, unused) = CoverageMath.Measure([new(0, 100, 1), new(20, 60, 0), new(30, 40, 1)]);

        length.Should().Be(100);
        unused.Should().Be(30);
        CoverageMath.Measure([]).Should().Be((0, 0));
    }

    [Fact]
    public void The_sample_takes_the_home_page_then_one_page_per_template()
    {
        PageResult Page(string path, int depth, int status = 200, string? final = null) => new()
        {
            Id = "r/pages/" + path, Url = "https://a.test" + path, Depth = depth, StatusCode = status, Fingerprint = "00", FinalUrl = final,
        };
        var pages = new[]
        {
            Page("/blog/b", 1), Page("/blog/a", 1), Page("/about", 1), Page("/", 0), Page("/gone", 1, 404), Page("/old", 1, 200, "https://a.test/about"),
            Page("/products/12", 2), Page("/products/13", 2),
        };

        RenderSample.Pick(pages, 3).Select(p => p.Url).Should().Equal("https://a.test/", "https://a.test/about", "https://a.test/blog/a");
        RenderSample.Pick(pages, 10).Should().HaveCount(6, "404s and redirects are never sampled");
        RenderSample.Template(new Uri("https://a.test/products/12/reviews")).Should().Be("/products/#/*");
    }

    [Fact]
    public void The_report_is_printed_in_Dutch_with_every_value_encoded()
    {
        var run = new AuditRun
        {
            Id = "AuditRuns/1", Origin = "https://a.test", Score = 72, Grade = "C", PagesDone = 12, CompletedAtUtc = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc),
            CategoryScores = [new RunCategoryScore { Category = "Seo", Score = 72, Grade = "C" }],
            CheckResults =
            [
                new RunCheckResult { CheckId = "seo.title-present", Status = "Fail", Affected = 2, SampleUrls = ["https://a.test/<script>"] },
                new RunCheckResult { CheckId = "performance.lab-score", Status = "NotApplicable", Note = "PageSpeed Insights is not configured" },
            ],
        };
        var checks = new Dictionary<string, CheckDefinition>
        {
            ["seo.title-present"] = new() { CheckId = "seo.title-present", Category = "Seo", TitleEn = "Page has a title", TitleNl = "Pagina heeft een titel", HowToFixNl = "Zet een <title>." },
        };

        var html = ReportPage.Render(run, checks, "nl-BE", run.CompletedAtUtc.Value);

        html.Should().Contain("<html lang=\"nl\">");
        html.Should().Contain("Pagina heeft een titel");
        html.Should().Contain("Zet een &lt;title&gt;.");
        html.Should().Contain("https://a.test/&lt;script&gt;");
        html.Should().NotContain("<script");
        html.Should().Contain("Niet gemeten");
        html.Should().Contain("PageSpeed Insights is not configured");
        ReportPage.Render(run, checks, "en", run.CompletedAtUtc.Value).Should().Contain("Page has a title");
    }
}
