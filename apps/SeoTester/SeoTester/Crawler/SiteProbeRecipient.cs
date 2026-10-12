using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;
using SeoTester.Checks;
using SeoTester.Crawling;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.Messaging;

namespace SeoTester.Crawler;

/// <summary>
/// <c>seo-probe</c> (<c>docs/seo_analyzer_PRD.md</c> §4.3): the run's first stage. robots.txt decides first (Q-M8e): a
/// site that opted out, disallows everyone or whose robots.txt is unreachable ends the run as <c>Blocked</c> before
/// any page is fetched (D19). Otherwise the probe measures the site-level checks, crawls the home page, seeds the
/// frontier with its links and the sitemap's URLs, and chains batch 0 — the frontier, the home page's result, the
/// progress and the message in one commit. A retry after that commit finds the frontier and does nothing.
/// </summary>
public partial class SiteProbeRecipient : IRecipient<SiteProbeMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageOutbox outbox;
    [Inject] private readonly IMessageContext context;
    [Inject] private readonly SiteProber prober;
    [Inject] private readonly RunPipeline pipeline;
    [Inject] private readonly TimeProvider time;

    public async Task HandleAsync(SiteProbeMessage message, CancellationToken cancellationToken = default)
    {
        if (await RunPipeline.LoadActiveAsync(session, message.RunId, cancellationToken) is not { } run)
            return;
        if (await session.LoadAsync<CrawlFrontier>(CrawlFrontier.IdFor(run.Id!), cancellationToken) is not null)
            return; // probed already: this is a redelivery
        if (run.CancelRequested)
        {
            await pipeline.EndAsync(run, AuditRunStatus.Cancelled, null, null, cancellationToken);
            return;
        }

        await pipeline.GuardAsync(run, context, () => ProbeAsync(run, cancellationToken), cancellationToken);
    }

    private async Task ProbeAsync(AuditRun run, CancellationToken cancellationToken)
    {
        session.Advanced.Patch<AuditRun, string>(run.Id!, r => r.Status, AuditRunStatus.Running);
        RunPipeline.PatchProgress(session, run.Id!, "probe", 0, 0, run.Origin + "/robots.txt");
        await session.SaveChangesAsync(cancellationToken);

        var interval = await pipeline.IntervalAsync(run, null, cancellationToken);
        using var pacer = new HostPacer(run.ConnectionsPerHost, interval, null, time);
        var outcome = await prober.ProbeAsync(run, pacer, cancellationToken);

        if (outcome.Blocked)
        {
            var why = outcome.Robots.Availability == RobotsAvailability.Unreachable
                ? $"robots.txt could not be read ({outcome.RobotsFacts.Status?.ToString() ?? "no answer"}); RFC 9309 treats that as \"crawl nothing\", so nothing was crawled."
                : outcome.Robots.OptedOut
                    ? $"The site's robots.txt asks {SeoTesterBot.RobotsToken} not to crawl it, so nothing was crawled."
                    : "The site's robots.txt does not allow any crawler, so nothing was crawled.";
            await pipeline.EndAsync(run, AuditRunStatus.Blocked, why, AuditLogKinds.RobotsBlocked, cancellationToken);
            return;
        }

        var home = outcome.Home!;
        var now = pipeline.NowUtc;
        var frontier = new CrawlFrontier
        {
            Id = CrawlFrontier.IdFor(run.Id!),
            RunId = run.Id!,
            Origin = run.Origin,
            Host = new Uri(run.Origin).IdnHost,
            RobotsRules = outcome.Robots.Rules.ToList(),
            CrawlDelaySeconds = outcome.Robots.CrawlDelaySeconds,
            SitemapUrls = outcome.SitemapUrls.ToList(),
            SiteFindings = SiteChecks.Evaluate(outcome.Facts).Select(f => StoredFinding.From(f)).ToList(),
            Fetched = 1,
            Bytes = home.Input.BodyBytes,
            Errors = home.Result.StatusCode is 0 or >= 500 ? 1 : 0,
            LastRequestAtUtc = pacer.LastStartUtc,
            ExpiresAtUtc = now + TimeSpan.FromDays(2),
        };
        frontier.Seen.Add(CrawlUrl.Hash(home.Result.Url));
        frontier.Stored.Add(CrawlUrl.Hash(home.Result.Url));
        CrawlFrontiers.Extend(frontier, home, maxQueued: run.MaxPages, maxExternal: run.DeepCrawl ? pipeline.Options.MaxExternalLinks : 0);
        foreach (var url in outcome.SitemapUrls)
            if (frontier.Seen.Add(CrawlUrl.Hash(url)) && CrawlFrontiers.LooksLikePage(url))
                frontier.Pending.Add(new FrontierEntry(url, -1, null, FromSitemap: true));
        if (outcome.SitemapUrls.Contains(home.Result.Url))
            home.Result.InSitemap = true;

        await session.StoreAsync(home.Result, cancellationToken);
        await session.StoreAsync(frontier, string.Empty, frontier.Id, cancellationToken); // "must not exist": one probe wins
        RunPipeline.PatchProgress(session, run.Id!, "crawl", 1, frontier.Seen.Count, home.Result.Url);
        await outbox.EnqueueAsync(session, new PageBatchMessage(run.Id!, 0), cancellationToken: cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }
}
