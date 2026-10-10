using System.Diagnostics;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;
using SeoTester.Checks;
using SeoTester.Crawling;
using SeoTester.Entities;
using SeoTester.Fetching;
using SeoTester.LookupReferences;
using SeoTester.Messaging;

namespace SeoTester.Crawler;

/// <summary>
/// <c>seo-crawl</c> (<c>docs/seo_analyzer_PRD.md</c> §4.3, §6, spike S7): fetches the next batch of the frontier
/// through the SSRF-safe client, politely (the tier's connections per host and interval, robots.txt Disallow and
/// Crawl-delay, <c>Retry-After</c>), judges each page, and commits the page results, the advanced frontier, the
/// progress and the next message together. The frontier is written with its change vector, so a duplicate delivery
/// of the same batch cannot commit twice, and a batch killed half-way is simply fetched again (page ids are
/// deterministic). The crawl stops at the run's page limit, its wall time (the full-crawl ceiling for a full crawl),
/// an empty frontier, a cancel, or a server that keeps answering errors; then the external links are checked (verified
/// runs) and the aggregate is enqueued.
/// </summary>
public partial class PageBatchRecipient : IRecipient<PageBatchMessage>
{
    /// <summary>A server that answers more than half of at least this many requests with 5xx or nothing is left alone.</summary>
    private const int ErrorRateMinimum = 10;

    /// <summary>How often one URL is asked again after a 429/503 with <c>Retry-After</c>; then its answer is recorded as it is.</summary>
    private const int MaxRetriesPerUrl = 2;

    /// <summary>A <c>Retry-After</c> longer than this ends the crawl instead of waiting.</summary>
    private static readonly TimeSpan LongestBackOff = TimeSpan.FromMinutes(5);

    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageOutbox outbox;
    [Inject] private readonly IMessageContext context;
    [Inject] private readonly ISeoFetcher fetcher;
    [Inject] private readonly RunPipeline pipeline;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<PageBatchRecipient> logger;

    public async Task HandleAsync(PageBatchMessage message, CancellationToken cancellationToken = default)
    {
        if (await RunPipeline.LoadActiveAsync(session, message.RunId, cancellationToken) is not { } run)
            return;
        var frontier = await session.LoadAsync<CrawlFrontier>(CrawlFrontier.IdFor(run.Id!), cancellationToken);
        if (frontier is null || frontier.NextBatch != message.BatchNo)
            return; // a redelivery of a batch that already committed
        // Written back with the version read here: of two deliveries of one batch, only one commits.
        var changeVector = session.Advanced.GetChangeVectorFor(frontier);
        session.Advanced.Evict(frontier);
        if (run.CancelRequested)
        {
            await pipeline.EndAsync(run, AuditRunStatus.Cancelled, null, null, cancellationToken); // logged by CancelAudit
            return;
        }

        await pipeline.GuardAsync(run, context, () => CrawlAsync(run, frontier, changeVector, message.BatchNo, cancellationToken), cancellationToken);
    }

    private async Task CrawlAsync(AuditRun run, CrawlFrontier frontier, string changeVector, int batchNo, CancellationToken cancellationToken)
    {
        var options = pipeline.Options;
        var deadline = (run.StartedAtUtc ?? pipeline.NowUtc).AddMinutes(run.WallTimeMinutes);
        var robots = frontier.Robots();
        var interval = await pipeline.IntervalAsync(run, frontier.CrawlDelaySeconds, cancellationToken);
        using var pacer = new HostPacer(run.ConnectionsPerHost, interval, frontier.LastRequestAtUtc, time);

        // Take this batch's URLs: allowed by robots.txt, within the page limit.
        var batch = new List<FrontierEntry>();
        while (frontier.Pending.Count > 0 && batch.Count < options.BatchSize && frontier.Fetched + batch.Count < run.MaxPages)
        {
            var entry = frontier.Pending[0];
            frontier.Pending.RemoveAt(0);
            if (Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) && robots.IsAllowed(uri))
                batch.Add(entry);
        }

        var results = new List<AnalyzedPage>();
        var requeue = new List<FrontierEntry>();
        TimeSpan? backOff = null;
        var budget = Stopwatch.StartNew();
        var lastProgress = Stopwatch.StartNew();
        var queue = new Queue<FrontierEntry>(batch);
        var gate = new object();

        async Task WorkerAsync()
        {
            while (true)
            {
                FrontierEntry entry;
                lock (gate)
                {
                    // Stop taking pages when the budget, the wall time or a back-off says so; they go back to the frontier.
                    if (queue.Count == 0)
                        return;
                    if (backOff is not null || budget.Elapsed > options.BatchBudget || pipeline.NowUtc >= deadline)
                    {
                        requeue.AddRange(queue);
                        queue.Clear();
                        return;
                    }
                    entry = queue.Dequeue();
                }

                FetchResult fetch;
                using (await pacer.WaitAsync(cancellationToken))
                    fetch = await fetcher.FetchAsync(new Uri(entry.Url), PageFetcher.MaxPageBytes, cancellationToken);

                // Back off and ask again later, but not for ever: a URL that keeps answering 429 would otherwise hold
                // the crawl until its wall time.
                if (fetch.StatusCode is 429 or 503 && fetch.RetryAfter is { } wait && entry.Retries < MaxRetriesPerUrl)
                {
                    lock (gate)
                    {
                        backOff = wait;
                        requeue.Insert(0, entry with { Retries = entry.Retries + 1 });
                    }
                    pacer.Defer(wait);
                    continue;
                }

                var linked = frontier.Linked.Contains(CrawlUrl.Hash(entry.Url));
                var page = PageAnalyzer.Analyze(fetch, entry, run, frontier.Host, linked, pipeline.NowUtc);
                lock (gate)
                    results.Add(page);

                if (lastProgress.Elapsed >= options.ProgressInterval)
                {
                    lastProgress.Restart();
                    int done;
                    lock (gate)
                        done = frontier.Fetched + results.Count;
                    await pipeline.WriteProgressAsync(run.Id!, "crawl", done, frontier.Seen.Count, entry.Url, cancellationToken);
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Max(1, run.ConnectionsPerHost)).Select(_ => WorkerAsync()));

        // Commit: pages, the frontier (in breadth-first order), progress, and what comes next.
        frontier.Pending.InsertRange(0, requeue);
        foreach (var page in results.OrderBy(p => batch.FindIndex(b => b.Url == p.Result.Url)))
        {
            await session.StoreAsync(page.Result, cancellationToken);
            CrawlFrontiers.Extend(frontier, page, run.MaxPages, run.DeepCrawl ? options.MaxExternalLinks : 0);
            frontier.Fetched++;
            frontier.Bytes += page.Input.BodyBytes;
            if (page.Result.StatusCode is 0 or >= 500)
                frontier.Errors++;
        }
        frontier.NextBatch = batchNo + 1;
        frontier.LastRequestAtUtc = pacer.LastStartUtc ?? frontier.LastRequestAtUtc;

        var stop = StopReason(run, frontier, deadline, backOff);
        var current = results.LastOrDefault()?.Result.Url;
        if (stop is null)
        {
            RunPipeline.PatchProgress(session, run.Id!, "crawl", frontier.Fetched, frontier.Seen.Count, current);
            var next = new PageBatchMessage(run.Id!, frontier.NextBatch);
            await outbox.EnqueueAsync(session, next, backOff is { } delay ? new BroadcastOptions { Delay = delay } : null, cancellationToken);
        }
        else
        {
            frontier.StopReason = stop == "done" ? null : stop;
            if (run.DeepCrawl && frontier.External.Count > 0 && frontier.ExternalResults is null)
                frontier.ExternalResults = await CheckExternalLinksAsync(frontier.External, cancellationToken);
            RunPipeline.PatchProgress(session, run.Id!, "aggregate", frontier.Fetched, frontier.Seen.Count, null);
            session.Advanced.Patch<AuditRun, string>(run.Id!, r => r.StagesDone, stages => stages.Add(PipelineStages.Crawl));
            // The render and PageSpeed stages sample the crawled pages, so they start now, side by side; each enqueues
            // the aggregate as it finishes, and the aggregate waits for both (M10).
            await outbox.EnqueueAsync(session, new RenderSampleMessage(run.Id!), cancellationToken: cancellationToken);
            await outbox.EnqueueAsync(session, new PsiMessage(run.Id!), cancellationToken: cancellationToken);
        }

        await session.StoreAsync(frontier, changeVector, frontier.Id, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Run {RunId} batch {Batch}: {Pages} pages in {Ms} ms{Stop}", run.Id, batchNo, results.Count, budget.ElapsedMilliseconds,
            stop is null ? "" : ", crawl finished: " + stop);
    }

    /// <summary>Why the crawl ends after this batch, or null to go on.</summary>
    private string? StopReason(AuditRun run, CrawlFrontier frontier, DateTime deadline, TimeSpan? backOff)
    {
        if (backOff is { } wait && wait > LongestBackOff)
            return $"the server asked to wait {wait.TotalMinutes:0} minutes";
        if (frontier.Fetched >= ErrorRateMinimum && frontier.Errors * 2 > frontier.Fetched)
            return "the server answered most requests with an error";
        if (frontier.Fetched >= run.MaxPages)
            return frontier.Pending.Count > 0 ? $"page limit ({run.MaxPages})" : "done";
        if (pipeline.NowUtc >= deadline)
            return $"time limit ({run.WallTimeMinutes} minutes)";
        return frontier.Pending.Count == 0 ? "done" : null;
    }

    /// <summary>One HEAD per external host (GET when HEAD is refused), one at a time: a light touch on sites nobody asked us to audit.</summary>
    private async Task<List<ExternalLinkResult>> CheckExternalLinksAsync(IReadOnlyList<FrontierEntry> links, CancellationToken cancellationToken)
    {
        var results = new List<ExternalLinkResult>();
        foreach (var link in links)
        {
            var head = await fetcher.FetchAsync(new Uri(link.Url), 0, cancellationToken, HttpMethod.Head);
            if (head.StatusCode is 405 or 501)
                head = await fetcher.FetchAsync(new Uri(link.Url), 1024, cancellationToken);
            results.Add(new ExternalLinkResult(link.Url, link.Referrer ?? string.Empty, head.StatusCode == 0 ? null : head.StatusCode, head.Error));
        }
        return results;
    }
}

/// <summary>The stages a run passes before its aggregate (§4.3): the crawl, then the render and PageSpeed stages side by side.</summary>
public static class PipelineStages
{
    public const string Crawl = "crawl";
    public const string Render = "render";
    public const string Psi = "psi";

    /// <summary>Every stage the aggregate waits for.</summary>
    public static IReadOnlyList<string> BeforeAggregate { get; } = [Crawl, Render, Psi];
}
