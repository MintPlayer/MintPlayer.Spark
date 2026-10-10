using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using SeoTester.Catalogue;
using SeoTester.Checks;
using SeoTester.Crawling;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.Messaging;
using SeoTester.Scoring;

namespace SeoTester.Crawler;

/// <summary>
/// <c>seo-aggregate</c> (<c>docs/seo_analyzer_PRD.md</c> §4.3, §7): adds up the run's findings (every page's, the
/// probe's site-level ones, and the crawl-level checks over all pages: duplicates, link graph, redirects, sitemap
/// coverage, depth, near-duplicates, hreflang return links), scores them with the snapshot the run started with
/// (D32), compares each check with the site's previous completed run (New / Fixed / Unchanged), and in one commit
/// finishes the run, updates the site's latest score and deletes the frontier. Then the participants are notified
/// (X10). Pages are read by id prefix, not through an index: complete and never stale.
/// </summary>
public partial class AggregateRecipient : IRecipient<AggregateMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageContext context;
    [Inject] private readonly RunPipeline pipeline;
    [Inject] private readonly Catalogue.CatalogueSnapshots snapshots;

    public async Task HandleAsync(AggregateMessage message, CancellationToken cancellationToken = default)
    {
        if (await RunPipeline.LoadActiveAsync(session, message.RunId, cancellationToken) is not { } run)
            return;
        await pipeline.GuardAsync(run, context, () => AggregateAsync(run, cancellationToken), cancellationToken);
    }

    private async Task AggregateAsync(AuditRun run, CancellationToken cancellationToken)
    {
        var frontier = await session.LoadAsync<CrawlFrontier>(CrawlFrontier.IdFor(run.Id!), cancellationToken);
        var pages = new List<PageResult>();
        await using (var stream = await session.Advanced.StreamAsync<PageResult>(PageResult.PrefixFor(run.Id!), token: cancellationToken))
            while (await stream.MoveNextAsync())
                pages.Add(stream.Current.Document);

        var tally = new RunTally();
        foreach (var page in pages)
            foreach (var finding in page.Findings)
                tally.Add(page.Url, new CheckFinding(finding.CheckId, Enum.TryParse<CheckStatus>(finding.Status, out var s) ? s : CheckStatus.Error, CheckFinding.ParseDetail(finding.Detail)));
        foreach (var stored in frontier?.SiteFindings ?? [])
            tally.Add(stored.Url, stored.ToFinding());

        var linked = frontier?.Linked ?? [];
        var crawled = pages.Select(p => PageAnalyzer.ToCrawled(p) with { LinkedInternally = linked.Contains(CrawlUrl.Hash(p.Url)) }).ToList();
        foreach (var finding in CrawlChecks.Evaluate(crawled, frontier?.SitemapUrls ?? [], frontier?.ExternalResults, url => linked.Contains(CrawlUrl.Hash(url))))
            tally.Add(finding.Url, finding.Finding);

        var tallies = tally.Tallies();
        var snapshot = run.ScoringSnapshot ?? await snapshots.CreateAsync(session, cancellationToken);
        var card = ScoreCalculator.Score(snapshot, tallies.Values.Select(t => t.Outcome));
        var previous = await PreviousResultsAsync(run, cancellationToken);

        var checkResults = card.Checks.Select(c => new RunCheckResult
        {
            CheckId = c.CheckId,
            Status = c.Status.ToString(),
            Affected = c.Affected,
            SampleUrls = tallies.TryGetValue(c.CheckId, out var t) ? t.SampleUrls.ToList() : [],
            Delta = previous is null ? null : DeltaOf(previous.GetValueOrDefault(c.CheckId), c.Status),
        }).ToList();
        var categoryScores = card.Categories.Select(c => new RunCategoryScore
        {
            Category = c.Category.ToString(),
            Score = c.Score,
            Grade = c.Grade,
            Capped = c.Capped,
        }).ToList();

        var now = pipeline.NowUtc;
        var id = run.Id!;
        session.Advanced.Patch<AuditRun, string>(id, r => r.Status, AuditRunStatus.Completed);
        RunPipeline.PatchProgress(session, id, "done", frontier?.Fetched ?? pages.Count, frontier?.Seen.Count ?? pages.Count, null);
        session.Advanced.Patch<AuditRun, DateTime?>(id, r => r.CompletedAtUtc, now);
        session.Advanced.Patch<AuditRun, int?>(id, r => r.Score, card.Overall);
        session.Advanced.Patch<AuditRun, string>(id, r => r.Grade!, card.Grade!);
        session.Advanced.Patch<AuditRun, List<RunCategoryScore>>(id, r => r.CategoryScores, categoryScores);
        session.Advanced.Patch<AuditRun, List<RunCheckResult>>(id, r => r.CheckResults, checkResults);
        session.Advanced.Patch<AuditRun, string>(id, r => r.Error!, frontier?.StopReason is { } stop ? "The crawl stopped early: " + stop + "." : null!);
        if (run.SiteId is { } siteId)
        {
            session.Advanced.Patch<Site, string>(siteId, s => s.LatestRunId!, id);
            session.Advanced.Patch<Site, int?>(siteId, s => s.LatestScore, card.Overall);
            session.Advanced.Patch<Site, string>(siteId, s => s.LatestGrade!, card.Grade!);
            session.Advanced.Patch<Site, DateTime?>(siteId, s => s.LatestRunAtUtc, now);
        }
        if (frontier is not null)
            session.Delete(frontier);
        await session.SaveChangesAsync(cancellationToken);

        await pipeline.NotifyAsync(run,
            card.Overall is { } score ? $"Audit of {run.Origin} finished: {score} ({card.Grade})" : $"Audit of {run.Origin} finished",
            $"{pages.Count} pages checked; {checkResults.Count(r => r.Status == nameof(CheckStatus.Fail))} checks failed.", "audit-completed", cancellationToken);
    }

    /// <summary>The check statuses of the site's latest completed run before this one, or null when there is none.</summary>
    private async Task<Dictionary<string, string>?> PreviousResultsAsync(AuditRun run, CancellationToken cancellationToken)
    {
        if (run.SiteId is not { } siteId)
            return null;
        var id = run.Id;
        var previous = await session.Query<AuditRun>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(r => r.SiteId == siteId && r.Status == AuditRunStatus.Completed && r.Id != id)
            .OrderByDescending(r => r.CompletedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return previous?.CheckResults.GroupBy(r => r.CheckId).ToDictionary(g => g.Key, g => g.First().Status, StringComparer.Ordinal);
    }

    /// <summary>New: flagged now but not before; Fixed: flagged before, passing now; Unchanged otherwise.</summary>
    public static string DeltaOf(string? before, CheckStatus now)
    {
        var flaggedNow = now is CheckStatus.Fail or CheckStatus.Warn;
        var flaggedBefore = before is nameof(CheckStatus.Fail) or nameof(CheckStatus.Warn);
        return flaggedNow && !flaggedBefore ? "New" : !flaggedNow && flaggedBefore && now == CheckStatus.Pass ? "Fixed" : "Unchanged";
    }
}
