using System.Net;
using MintPlayer.Spark.Notifications.Models;
using Raven.Client.Documents;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.Settings;
using SeoTester.Tests._Infrastructure;
using static SeoTester.Tests._Infrastructure.SeoApi;

namespace SeoTester.Tests.Crawler;

/// <summary>
/// The crawl pipeline end to end (<c>docs/seo_analyzer_PRD.md</c> §4.3, §6, M9): StartAudit → <c>seo-probe</c> →
/// <c>seo-crawl</c> batches → <c>seo-aggregate</c>, on a host of its own whose web, DNS, TLS and registries are fakes
/// (nothing leaves the machine). The crawl is tuned fast here (20 ms between requests, batches of 4 pages) so that
/// several batches chain within a test.
/// </summary>
[Collection(SeoPipelineHostCollection.Name)]
public class CrawlPipelineTests(SeoPipelineHostFixture host)
{
    private async Task<string> StartAsync(Site site, string user = Max)
    {
        await SetSettingAsync(host, SeoQuotas.Audits.Limit.Key, "-1");
        await SetSettingAsync(host, SeoQuotas.VerifiedAudits.Limit.Key, "-1");
        var (status, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "StartAudit", site.Id!, user);
        status.Should().Be(HttpStatusCode.OK, raw);
        return body.GetProperty("result").GetProperty("runId").GetString()!;
    }

    /// <summary>
    /// /, /a and /b (same title), /old (301 to /a), /missing (404, linked), /orphan (sitemap only), /private (disallowed by
    /// robots.txt, linked from the home page) and a link to another site, <paramref name="externalHost"/>: a verified site's
    /// run checks it, and the fake web is the collection's, so a test that counts those checks owns that host.
    /// </summary>
    private string BuildSite(string? hostName = null, string robots = "User-agent: *\nDisallow: /private\n", string externalHost = "external.example.org")
    {
        var h = hostName ?? FakeSite.NewHost();
        var o = "https://" + h;
        host.Web.Page(o + "/robots.txt", robots + $"Sitemap: {o}/sitemap.xml\n");
        host.Web.Pages[o + "/sitemap.xml"] = new FakeResponse(HttpStatusCode.OK,
            $"<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"><url><loc>{o}/</loc></url><url><loc>{o}/a</loc></url><url><loc>{o}/orphan</loc></url></urlset>",
            ContentType: "application/xml");
        host.Web.Html(o + "/", FakeSite.Page("Home of the example shop", "/a", "/b", "/old", "/missing", "/private/secret", $"https://{externalHost}/page"),
            new Dictionary<string, string> { ["Strict-Transport-Security"] = "max-age=63072000", ["X-Content-Type-Options"] = "nosniff" });
        host.Web.Html(o + "/a", FakeSite.Page("Same title on two pages", "/"));
        host.Web.Html(o + "/b", FakeSite.Page("Same title on two pages", "/"));
        host.Web.Redirect(o + "/old", o + "/a", HttpStatusCode.MovedPermanently);
        host.Web.Html(o + "/orphan", FakeSite.Page("Nobody links here"));
        host.Web.Html(o + "/private/secret", FakeSite.Page("Must never be fetched"));
        host.Web.Page(o + "/.git/HEAD", "ref: refs/heads/main\n");
        host.Web.Html($"https://{externalHost}/page", "<html></html>");
        FakeSite.Registries(host.Web);
        return h;
    }

    [Fact]
    public async Task An_audit_probes_crawls_in_batches_scores_and_finishes_the_run()
    {
        var site = await SeedSiteAsync(host, Acme, hostName: BuildSite());
        var runId = await StartAsync(site);

        var run = await FakeSite.FinishedAsync(host.Database, runId);

        run.Status.Should().Be(AuditRunStatus.Completed, run.Error);
        run.Score.HasValue.Should().BeTrue();
        run.Grade.Should().NotBeNull();
        run.PagesDone.Should().Be(6, "/, /a, /b, /old, /missing, /orphan: /private is disallowed");
        run.CompletedAtUtc.HasValue.Should().BeTrue();
        var results = run.CheckResults.ToDictionary(r => r.CheckId);
        results["seo.duplicate-titles"].Status.Should().Be("Fail");
        results["seo.duplicate-titles"].SampleUrls.Should().BeEquivalentTo([site.Origin + "/a", site.Origin + "/b"]);
        results["technical.broken-internal-links"].SampleUrls.Should().Equal(site.Origin + "/missing");
        results["technical.orphan-pages"].SampleUrls.Should().Equal(site.Origin + "/orphan");
        results["technical.redirect-chains"].SampleUrls.Should().Equal(site.Origin + "/old");
        results["domain.expiry"].Status.Should().Be("Pass", "the recorded .com RDAP answer expires in 2028");
        results["security.exposed-files"].Status.Should().Be("NotApplicable", "an unverified site gets no intrusive probes");
        // M10: without a PageSpeed key or a browser (this host has neither), those checks are not measured, with why.
        results["performance.core-web-vitals"].Status.Should().Be("NotApplicable");
        results["performance.core-web-vitals"].Note.Should().Be("PageSpeed Insights is not configured");
        results["accessibility.color-contrast"].Status.Should().Be("NotApplicable");
        results["accessibility.color-contrast"].Note.Should().Be("the headless browser is not configured");
        run.CategoryScores.Should().Contain(c => c.Category == "Seo" && c.Score != null);
        run.Delta().Should().BeEmpty("the first run has nothing to compare with");

        var requests = host.Web.RequestsTo(site.Host).ToList();
        requests.Should().OnlyContain(r => r.UserAgent == SeoTesterBot.UserAgent);
        requests.Select(r => new Uri(r.Url).AbsolutePath).Should().NotContain("/private/secret").And.NotContain("/.git/HEAD");
        requests.Should().Contain(r => r.Url.EndsWith("/robots.txt", StringComparison.Ordinal));
        requests.First().Url.Should().EndWith("/robots.txt", "robots.txt is read before anything else (Q-M8e)");
        host.Web.RequestsTo("external.example.org").Should().BeEmpty("external links are checked for verified sites only");

        using var session = host.Database.OpenAsyncSession();
        var pages = await session.Advanced.LoadStartingWithAsync<PageResult>(PageResult.PrefixFor(site.Id!), pageSize: 100);
        pages.Should().HaveCount(6);
        pages.Should().OnlyContain(p => p.OrganizationId == Acme && p.ExpiresAtUtc > DateTime.UtcNow.AddDays(6));
        pages.Single(p => p.Url == site.Origin + "/").Findings.Should().Contain(f => f.CheckId == "seo.title-present" && f.Status == "Pass");
        (await session.LoadAsync<CrawlFrontier>(CrawlFrontier.IdFor(runId))).Should().BeNull("the frontier is deleted on completion");
        var stored = await session.LoadAsync<Site>(site.Id);
        stored.LatestRunId.Should().Be(runId);
        stored.LatestScore.Should().Be(run.Score);
        (await session.Query<Notification>().Customize(c => c.WaitForNonStaleResults()).Where(n => n.UserId == Max && n.Url!.EndsWith(Uri.EscapeDataString(runId))).ToListAsync())
            .Should().ContainSingle("the participant is told")
            .Which.Url.Should().Be("/po/audit-run/" + Uri.EscapeDataString(runId), "the SPA resolves a type by its alias or id (M11): /po/auditrun/… opened nothing");
    }

    [Fact]
    public async Task A_site_that_opts_out_in_robots_txt_is_never_crawled()
    {
        var site = await SeedSiteAsync(host, Acme, hostName: BuildSite(robots: "User-agent: MintPlayerSeoTester\nDisallow: /\n\n"));
        var runId = await StartAsync(site);

        var run = await FakeSite.FinishedAsync(host.Database, runId);

        run.Status.Should().Be(AuditRunStatus.Blocked);
        run.Error.Should().Contain("asks MintPlayerSeoTester not to crawl it");
        run.Score.HasValue.Should().BeFalse();
        host.Web.RequestsTo(site.Host).Select(r => new Uri(r.Url).AbsolutePath).Should().Equal("/robots.txt");
        using var session = host.Database.OpenAsyncSession();
        (await session.Query<AuditLogEntry>().Customize(c => c.WaitForNonStaleResults()).Where(e => e.RunId == runId && e.Kind == AuditLogKinds.RobotsBlocked).CountAsync())
            .Should().Be(1);
    }

    [Fact]
    public async Task An_unreachable_robots_txt_means_crawl_nothing()
    {
        var hostName = BuildSite();
        host.Web.Pages[$"https://{hostName}/robots.txt"] = new FakeResponse(HttpStatusCode.ServiceUnavailable, "down");
        var site = await SeedSiteAsync(host, Acme, hostName: hostName);

        var run = await FakeSite.FinishedAsync(host.Database, await StartAsync(site));

        run.Status.Should().Be(AuditRunStatus.Blocked);
        run.Error.Should().Contain("could not be read (503)");
        host.Web.RequestsTo(hostName).Should().ContainSingle();
    }

    [Fact]
    public async Task A_member_cancels_a_running_audit_and_the_crawler_stops_at_its_next_batch()
    {
        var hostName = FakeSite.NewHost();
        var origin = "https://" + hostName;
        var links = Enumerable.Range(0, 30).Select(i => $"/p{i}").ToArray();
        host.Web.Html(origin + "/", FakeSite.Page("Slow home", links));
        foreach (var link in links)
            host.Web.Html(origin + link, FakeSite.Page("Slow page " + link), delay: TimeSpan.FromMilliseconds(400));
        var site = await SeedSiteAsync(host, Acme, hostName: hostName);
        var runId = await StartAsync(site);
        await FakeSite.WaitForAsync(host.Database, runId, r => r.Phase == "crawl", TimeSpan.FromSeconds(60));

        var (status, _, raw) = await ActionAsync(host, AuditRunTypeId, "AuditRun", "CancelAudit", runId, Max);
        status.Should().Be(HttpStatusCode.OK, raw);
        var run = await FakeSite.FinishedAsync(host.Database, runId);

        run.Status.Should().Be(AuditRunStatus.Cancelled);
        run.Score.HasValue.Should().BeFalse();
        run.PagesDone.Should().BeLessThan(31);
        using var session = host.Database.OpenAsyncSession();
        (await session.LoadAsync<CrawlFrontier>(CrawlFrontier.IdFor(runId))).Should().BeNull();
        (await session.Query<AuditLogEntry>().Customize(c => c.WaitForNonStaleResults()).Where(e => e.RunId == runId && e.Kind == AuditLogKinds.AuditCancelled).CountAsync())
            .Should().Be(1);
    }

    [Fact]
    public async Task A_viewer_cannot_cancel_and_a_finished_run_cannot_be_cancelled()
    {
        var site = await SeedSiteAsync(host, Acme, hostName: BuildSite(robots: "User-agent: *\nDisallow: /\n"));
        var runId = await StartAsync(site);
        await FakeSite.FinishedAsync(host.Database, runId);

        (await ActionAsync(host, AuditRunTypeId, "AuditRun", "CancelAudit", runId, Vic)).Status.Should().NotBe(HttpStatusCode.OK);
        var (status, _, raw) = await ActionAsync(host, AuditRunTypeId, "AuditRun", "CancelAudit", runId, Max);
        // M11: a finished run withholds the action (403, the page shows no button); CancelAudit's own
        // "already finished" answer stays for a run that finishes between loading the page and the click.
        status.Should().Be(HttpStatusCode.Forbidden, raw);
        raw.Should().Contain("CancelAudit");
    }

    [Fact]
    public async Task An_unverified_site_gets_one_connection_and_the_request_interval()
    {
        await SetSettingAsync(host, SeoSettings.UnverifiedRequestInterval.Key, "00:00:00.300");
        try
        {
            var hostName = FakeSite.NewHost();
            var origin = "https://" + hostName;
            var links = Enumerable.Range(0, 6).Select(i => $"/q{i}").ToArray();
            host.Web.Html(origin + "/", FakeSite.Page("Polite home", links));
            foreach (var link in links)
                host.Web.Html(origin + link, FakeSite.Page("Polite " + link), delay: TimeSpan.FromMilliseconds(50));
            var site = await SeedSiteAsync(host, Acme, hostName: hostName);

            var run = await FakeSite.FinishedAsync(host.Database, await StartAsync(site));

            run.Status.Should().Be(AuditRunStatus.Completed, run.Error);
            host.Web.MaxInFlight[hostName].Should().Be(1);
            var starts = host.Web.RequestsTo(hostName).Select(r => r.StartedUtc).Order().ToList();
            starts.Zip(starts.Skip(1), (a, b) => (b - a).TotalMilliseconds).Should().OnlyContain(gap => gap >= 280,
                "request starts are at least the interval apart, also across batches");
        }
        finally
        {
            await SetSettingAsync(host, SeoSettings.UnverifiedRequestInterval.Key, null);
        }
    }

    [Fact]
    public async Task A_retry_after_is_waited_for_but_a_url_that_keeps_refusing_does_not_hold_the_crawl()
    {
        var hostName = FakeSite.NewHost();
        var origin = "https://" + hostName;
        host.Web.Html(origin + "/", FakeSite.Page("Busy home", "/busy", "/fine"));
        host.Web.Pages[origin + "/busy"] = new FakeResponse(HttpStatusCode.TooManyRequests, "slow down", Headers: new Dictionary<string, string> { ["Retry-After"] = "1" });
        host.Web.Html(origin + "/fine", FakeSite.Page("Fine page"));
        var site = await SeedSiteAsync(host, Acme, hostName: hostName);

        var run = await FakeSite.FinishedAsync(host.Database, await StartAsync(site));

        run.Status.Should().Be(AuditRunStatus.Completed, run.Error);
        var busy = host.Web.RequestsTo(hostName).Where(r => r.Url == origin + "/busy").Select(r => r.StartedUtc).Order().ToList();
        busy.Should().HaveCount(3, "asked once and retried twice, then recorded as it is");
        busy.Zip(busy.Skip(1), (a, b) => (b - a).TotalMilliseconds).Should().OnlyContain(gap => gap >= 950, "Retry-After: 1 is waited for");
        using var session = host.Database.OpenAsyncSession();
        (await session.LoadAsync<PageResult>(PageResult.IdFor(site.Id!, origin + "/busy"))).StatusCode.Should().Be(429);
    }

    [Fact]
    public async Task A_verified_site_gets_the_intrusive_probes_the_external_link_check_and_a_delta_on_its_next_run()
    {
        var hostName = BuildSite();
        var site = await SeedSiteAsync(host, Acme, verified: true, hostName: hostName);

        var first = await FakeSite.FinishedAsync(host.Database, await StartAsync(site));

        first.Status.Should().Be(AuditRunStatus.Completed, first.Error);
        first.IntrusiveProbes.Should().BeTrue();
        var results = first.CheckResults.ToDictionary(r => r.CheckId);
        results["security.exposed-files"].Status.Should().Be("Fail");
        results["security.exposed-files"].SampleUrls.Should().BeEmpty("a site-level finding names its file in the detail, not a page");
        results["technical.broken-external-links"].Status.Should().Be("Pass");
        host.Web.RequestsTo("external.example.org").Should().ContainSingle(r => r.Method == "HEAD");
        host.Web.RequestsTo(hostName).Should().Contain(r => r.Url.EndsWith("/.git/HEAD", StringComparison.Ordinal));

        // The owner fixes the duplicate title and removes the exposed file; the next run says what changed.
        host.Web.Html($"https://{hostName}/b", FakeSite.Page("A title of its own", "/"));
        host.Web.Pages.TryRemove($"https://{hostName}/.git/HEAD", out _);
        var second = await FakeSite.FinishedAsync(host.Database, await StartAsync(site));

        var deltas = second.CheckResults.ToDictionary(r => r.CheckId, r => r.Delta);
        deltas["seo.duplicate-titles"].Should().Be("Fixed");
        deltas["security.exposed-files"].Should().Be("Fixed");
        deltas["technical.orphan-pages"].Should().Be("Unchanged");
    }

    /// <summary>
    /// One result per page and site, the latest (owner decision 2026-10-12): a new scan's fetch of a page replaces the earlier
    /// scan's result instead of adding a copy of its own, a page the new scan no longer reaches keeps its earlier result until
    /// it expires, and each scan is scored from the pages it fetched, never from what an earlier scan left. What a scan
    /// measured stays on its run (totals, samples, the delta), so the history needs no old page.
    /// </summary>
    [Fact]
    public async Task A_new_scan_replaces_each_pages_result_and_counts_only_the_pages_it_fetched()
    {
        var hostName = BuildSite(externalHost: FakeSite.NewHost());
        var origin = "https://" + hostName;
        var site = await SeedSiteAsync(host, Acme, verified: true, hostName: hostName);
        var first = await FakeSite.FinishedAsync(host.Database, await StartAsync(site));
        first.Status.Should().Be(AuditRunStatus.Completed, first.Error);
        first.PagesDone.Should().Be(6, "/, /a, /b, /old, /missing, /orphan");

        // The owner trims the site: the home page links /a and /old only, and the sitemap lists / and /a.
        host.Web.Html(origin + "/", FakeSite.Page("Home of the example shop", "/a", "/old"));
        host.Web.Pages[origin + "/sitemap.xml"] = new FakeResponse(HttpStatusCode.OK,
            $"<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"><url><loc>{origin}/</loc></url><url><loc>{origin}/a</loc></url></urlset>",
            ContentType: "application/xml");
        var second = await FakeSite.FinishedAsync(host.Database, await StartAsync(site));
        second.Status.Should().Be(AuditRunStatus.Completed, second.Error);
        second.PagesDone.Should().Be(3, "/, /a and /old");

        using var session = host.Database.OpenAsyncSession();
        var pages = await session.Advanced.LoadStartingWithAsync<PageResult>(PageResult.PrefixFor(site.Id!), pageSize: 100);
        pages.Select(p => p.Url).Should().OnlyHaveUniqueItems().And.HaveCount(6, "one result per page of the site");
        pages.Where(p => p.RunId == second.Id).Select(p => p.Url).Should().BeEquivalentTo(
            [origin + "/", origin + "/a", origin + "/old"], because: "the second scan's fetches replaced the first scan's results");
        pages.Where(p => p.RunId == first.Id).Select(p => p.Url).Should().BeEquivalentTo(
            [origin + "/b", origin + "/missing", origin + "/orphan"], because: "a page the second scan did not reach keeps its latest result");
        pages.Should().OnlyContain(p => p.ExpiresAtUtc > DateTime.UtcNow.AddDays(6), "7 days from the fetch that wrote it");
        (await session.Advanced.LoadStartingWithAsync<PageResult>(first.Id + "/pages/", pageSize: 10)).Should().BeEmpty("no copy per scan");

        // Scored from its own three pages: /b (the duplicate title) is still stored, but it is the first scan's.
        var results = second.CheckResults.ToDictionary(r => r.CheckId);
        results["seo.duplicate-titles"].Status.Should().Be("Pass");
        results["seo.duplicate-titles"].Delta.Should().Be("Fixed", "compared with the first scan's totals, kept on its run");
        (await session.LoadAsync<AuditRun>(first.Id)).CheckResults.Select(r => (r.CheckId, r.Status, r.Affected, string.Join(' ', r.SampleUrls)))
            .Should().Equal(first.CheckResults.Select(r => (r.CheckId, r.Status, r.Affected, string.Join(' ', r.SampleUrls))), "a scan keeps what it measured");
    }
}

internal static class AuditRunTestExtensions
{
    public static IEnumerable<RunCheckResult> Delta(this AuditRun run) => run.CheckResults.Where(r => r.Delta is not null);
}

/// <summary>The pipeline's own host: a fast crawl (20 ms between requests to unverified hosts, 4 pages a batch).</summary>
public sealed class SeoPipelineHostFixture : SeoTesterHostFixture
{
    protected override IReadOnlyDictionary<string, string?>? HostSettings => new Dictionary<string, string?>
    {
        ["SeoTester:Limits:Unverified:RequestInterval"] = "00:00:00.020",
        ["SeoTester:Crawl:BatchSize"] = "4",
    };
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SeoPipelineHostCollection : ICollectionFixture<SeoPipelineHostFixture>
{
    public const string Name = "SeoTester pipeline host";
}
