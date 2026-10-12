using MintPlayer.Spark.Testing;
using SeoTester.Crawler;
using SeoTester.Crawling;
using SeoTester.Entities;

namespace SeoTester.Tests.Crawler;

/// <summary>
/// A site keeps one result per page, the latest (owner decision 2026-10-12), so two scans of one site that run at once (a full
/// crawl during an audit of a verified site, or two starts that race) write the same page documents. Each scan's tally must
/// still be whole: its pages are the ones it stored, whoever wrote them last, and never one an earlier scan left behind; and
/// its render and PageSpeed findings stay with it even when the other scan's fetch replaces the page they were added to.
/// </summary>
public class RunPagesTests : SparkTestDriver
{
    private const string SiteId = "Sites/1-A";
    private const string ThisRun = "AuditRuns/2-A";
    private const string ConcurrentRun = "AuditRuns/3-A";

    [Fact]
    public async Task A_runs_pages_are_those_it_stored_even_where_a_concurrent_scan_fetched_them_again()
    {
        var run = new AuditRun { Id = ThisRun, SiteId = SiteId };
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(Page(SiteId, "https://a.test/", ThisRun));
            await session.StoreAsync(Page(SiteId, "https://a.test/both", ConcurrentRun)); // fetched by this run, then again by the other
            await session.StoreAsync(Page(SiteId, "https://a.test/left", "AuditRuns/1-A")); // an earlier scan's; this run never fetched it
            await session.StoreAsync(Page("Sites/2-A", "https://a.test/", ThisRun)); // another site of the same origin
            await session.SaveChangesAsync();
        }
        var frontier = new CrawlFrontier { RunId = ThisRun };
        frontier.Stored.Add(CrawlUrl.Hash("https://a.test/"));
        frontier.Stored.Add(CrawlUrl.Hash("https://a.test/both"));

        using var read = Store.OpenAsyncSession();
        var pages = await RunPipeline.LoadRunPagesAsync(read, run, frontier, CancellationToken.None);

        pages.Select(p => p.Id).Order(StringComparer.Ordinal).Should().Equal(
            PageResult.IdFor(SiteId, "https://a.test/"), PageResult.IdFor(SiteId, "https://a.test/both"));
    }

    [Fact]
    public async Task A_sampled_finding_replaces_the_same_check_on_the_page_and_stays_with_its_run()
    {
        var run = new AuditRun { Id = ThisRun, SiteId = SiteId };
        var pageId = PageResult.IdFor(SiteId, "https://a.test/");
        using (var session = Store.OpenAsyncSession())
        {
            // The page as the concurrent scan left it: its own measurement of the render check.
            var page = Page(SiteId, "https://a.test/", ConcurrentRun);
            page.Findings = [new PageFinding { CheckId = "seo.title-present", Status = "Pass" }, new PageFinding { CheckId = "accessibility.color-contrast", Status = "Pass" }];
            await session.StoreAsync(page);
            await session.StoreAsync(new CrawlFrontier { Id = CrawlFrontier.IdFor(ThisRun), RunId = ThisRun });
            await session.SaveChangesAsync();
        }

        using (var session = Store.OpenAsyncSession())
        {
            RunPipeline.AddFindings(session, run, new Dictionary<string, List<PageFinding>>
            {
                ["https://a.test/"] = [new PageFinding { CheckId = "accessibility.color-contrast", Status = "Fail", Detail = "{\"n\":\"3\"}" }],
            }, []);
            await session.SaveChangesAsync();
        }

        using var read = Store.OpenAsyncSession();
        var stored = await read.LoadAsync<PageResult>(pageId);
        stored.Findings.Select(f => f.CheckId + "=" + f.Status).Should().Equal(
            ["seo.title-present=Pass", "accessibility.color-contrast=Fail"], "one finding per check: the latest measurement replaces the other scan's");
        var frontier = await read.LoadAsync<CrawlFrontier>(CrawlFrontier.IdFor(ThisRun));
        frontier.SampledFindings.Should().Equal(
            [new StoredFinding("accessibility.color-contrast", "Fail", "{\"n\":\"3\"}", "https://a.test/")], "the run scores its own measurement, whatever happens to the page");
    }

    private static PageResult Page(string owner, string url, string runId) => new()
    {
        Id = PageResult.IdFor(owner, url),
        RunId = runId,
        OrganizationId = "Organizations/acme",
        Url = url,
        StatusCode = 200,
        ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
    };
}
