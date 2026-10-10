using System.Net;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.Settings;
using SeoTester.Tests._Infrastructure;
using static SeoTester.Tests._Infrastructure.SeoApi;

namespace SeoTester.Tests.Crawler;

/// <summary>
/// Spike S7 (<c>docs/seo_analyzer_PRD.md</c> §8): the host stops in the middle of a crawl batch and a new one finishes
/// the run. The batch's pages, the advanced frontier and the next message commit together, so the half-done batch
/// left nothing behind and is fetched again; page ids are deterministic, so nothing is stored twice.
/// <para>
/// Messaging drains running handlers on a graceful stop, so a page that hangs (/k5, 60 s) holds the batch past the
/// host's shutdown timeout (2 s here): the stop then cancels the handler, as a killed container would end it.
/// </para>
/// </summary>
[Collection(SeoRestartHostCollection.Name)]
public class CrawlResumeTests(SeoRestartHostFixture host)
{
    [Fact]
    public async Task A_crawl_interrupted_mid_batch_resumes_on_the_next_host_without_duplicates()
    {
        await SetSettingAsync(host, SeoQuotas.Audits.Limit.Key, "-1");
        var hostName = FakeSite.NewHost();
        var origin = "https://" + hostName;
        var links = Enumerable.Range(0, 20).Select(i => $"/k{i}").ToArray();
        host.Web.Html(origin + "/", FakeSite.Page("Resumable home", links));
        foreach (var link in links)
            host.Web.Html(origin + link, FakeSite.Page("Resumable " + link), delay: TimeSpan.FromMilliseconds(link == "/k5" ? 60_000 : 100));
        var site = await SeedSiteAsync(host, Acme, hostName: hostName);
        var (status, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "StartAudit", site.Id!, Max);
        status.Should().Be(HttpStatusCode.OK, raw);
        var runId = body.GetProperty("result").GetProperty("runId").GetString()!;

        // Wait until batch 0 is half-way: /k0../k4 fetched, /k5 hanging, nothing of the batch committed.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!host.Web.RequestsTo(hostName).Any(r => r.Url == origin + "/k4") && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        await Task.Delay(300);
        using (var session = host.Database.OpenAsyncSession())
            (await session.Advanced.LoadStartingWithAsync<PageResult>(PageResult.PrefixFor(runId), pageSize: 100)).Should().HaveCount(1, "only the home page (the probe's) is committed");

        await host.RestartHostAsync();
        host.Web.Html(origin + "/k5", FakeSite.Page("Resumable /k5"), delay: TimeSpan.FromMilliseconds(100));
        var run = await FakeSite.FinishedAsync(host.Database, runId, TimeSpan.FromSeconds(120));

        run.Status.Should().Be(AuditRunStatus.Completed, run.Error);
        run.PagesDone.Should().Be(21);
        using (var session = host.Database.OpenAsyncSession())
        {
            var pages = await session.Advanced.LoadStartingWithAsync<PageResult>(PageResult.PrefixFor(runId), pageSize: 100);
            pages.Select(p => p.Url).Should().OnlyHaveUniqueItems();
            pages.Should().HaveCount(21);
        }
        host.Web.RequestsTo(hostName).Count(r => r.Url == origin + "/k0").Should().BeGreaterThanOrEqualTo(2, "the interrupted batch was fetched again");
    }
}

/// <summary>A host whose message claims expire quickly, so a run interrupted by a restart is picked up within seconds.</summary>
public sealed class SeoRestartHostFixture : SeoTesterHostFixture
{
    protected override IReadOnlyDictionary<string, string?>? HostSettings => new Dictionary<string, string?>
    {
        ["SeoTester:Limits:Unverified:RequestInterval"] = "00:00:00.020",
        ["SeoTester:Crawl:BatchSize"] = "10",
        ["Spark:Messaging:ClaimTtl"] = "00:00:04",
        ["Spark:Messaging:ClaimRenewInterval"] = "00:00:01",
        ["shutdownTimeoutSeconds"] = "2",
    };
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SeoRestartHostCollection : ICollectionFixture<SeoRestartHostFixture>
{
    public const string Name = "SeoTester restart host";
}
