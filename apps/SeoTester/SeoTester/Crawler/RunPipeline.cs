using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Notifications;
using MintPlayer.Spark.Settings;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using SeoTester.Audits;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.Settings;

namespace SeoTester.Crawler;

/// <summary>The crawl's tuning (configuration <c>SeoTester:Crawl</c>); the per-tier limits are settings (§6).</summary>
public sealed class CrawlOptions
{
    /// <summary>Most pages one <c>PageBatchMessage</c> fetches (spike S7: well under the queue's 10-minute handler timeout).</summary>
    public int BatchSize { get; set; } = 25;

    /// <summary>A batch takes no new page after this long, whatever <see cref="BatchSize"/> says, so a slow site cannot run into the handler timeout.</summary>
    public TimeSpan BatchBudget { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Links to other sites checked per run (verified sites only, one HEAD per external host).</summary>
    public int MaxExternalLinks { get; set; } = 50;

    /// <summary>Least time between two progress writes on the run (§4.4: at most every 2 s).</summary>
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// What the three recipients share (<c>docs/seo_analyzer_PRD.md</c> §4.3, §4.4): the run is written only by
/// <b>patches</b>, so the pipeline's writes never overwrite a concurrent join (participants) or cancel
/// (<c>CancelRequested</c>), and the live push (X4, "live": true) sees each change. Also the run's endings that are
/// not a score: cancelled, blocked by robots.txt, failed.
/// </summary>
public sealed class RunPipeline(
    IDocumentStore store,
    ISparkSettings settings,
    ISparkNotifier notifier,
    AuditLog auditLog,
    IOptions<SparkMessagingOptions> messaging,
    IOptions<CrawlOptions> crawlOptions,
    TimeProvider time,
    ILogger<RunPipeline> logger)
{
    public CrawlOptions Options => crawlOptions.Value;

    public DateTime NowUtc => time.GetUtcNow().UtcDateTime;

    /// <summary>The run, untracked (evicted), or null when it is gone or over.</summary>
    public static async Task<AuditRun?> LoadActiveAsync(IAsyncDocumentSession session, string runId, CancellationToken cancellationToken)
    {
        var run = await session.LoadAsync<AuditRun>(runId, cancellationToken);
        if (run is null)
            return null;
        session.Advanced.Evict(run);
        return AuditRunStatus.Finished.Contains(run.Status) ? null : run;
    }

    /// <summary>The least time between two request starts for the run's tier, or the site's Crawl-delay when larger (capped at 30 s).</summary>
    public async Task<TimeSpan> IntervalAsync(AuditRun run, double? crawlDelaySeconds, CancellationToken cancellationToken)
    {
        var interval = await settings.GetAsync(run.DeepCrawl ? SeoSettings.VerifiedRequestInterval : SeoSettings.UnverifiedRequestInterval, cancellationToken);
        if (crawlDelaySeconds is { } delay && TimeSpan.FromSeconds(Math.Min(delay, 30)) > interval)
            interval = TimeSpan.FromSeconds(Math.Min(delay, 30));
        return interval;
    }

    /// <summary>The run's progress fields, in the caller's unit of work.</summary>
    public static void PatchProgress(IAsyncDocumentSession session, string runId, string phase, int pagesDone, int discovered, string? currentUrl)
    {
        session.Advanced.Patch<AuditRun, string>(runId, r => r.Phase!, phase);
        session.Advanced.Patch<AuditRun, int>(runId, r => r.PagesDone, pagesDone);
        session.Advanced.Patch<AuditRun, int>(runId, r => r.PagesDiscovered, discovered);
        session.Advanced.Patch<AuditRun, string>(runId, r => r.CurrentUrl!, currentUrl!);
    }

    /// <summary>Writes the progress at once, in a session of its own (mid-batch, for the live view).</summary>
    public async Task WriteProgressAsync(string runId, string phase, int pagesDone, int discovered, string? currentUrl, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        PatchProgress(session, runId, phase, pagesDone, discovered, currentUrl);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Ends the run without a score, deletes its frontier, logs and tells the participants.</summary>
    public async Task EndAsync(AuditRun run, string status, string? error, string? logKind, CancellationToken cancellationToken)
    {
        using (var session = store.OpenAsyncSession())
        {
            session.Advanced.Patch<AuditRun, string>(run.Id!, r => r.Status, status);
            session.Advanced.Patch<AuditRun, string>(run.Id!, r => r.Phase!, status.ToLowerInvariant());
            session.Advanced.Patch<AuditRun, DateTime?>(run.Id!, r => r.CompletedAtUtc, NowUtc);
            session.Advanced.Patch<AuditRun, string>(run.Id!, r => r.Error!, error!);
            session.Advanced.Patch<AuditRun, string>(run.Id!, r => r.CurrentUrl!, null!);
            session.Delete(CrawlFrontier.IdFor(run.Id!));
            if (logKind is not null && run.SiteId is not null && await session.LoadAsync<Site>(run.SiteId, cancellationToken) is { } site)
                await session.StoreAsync(await auditLog.EntryAsync(logKind, run.StartedBy, site, run.Id, error, cancellationToken), cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }
        if (status != AuditRunStatus.Cancelled)
            await NotifyAsync(run, status == AuditRunStatus.Blocked ? $"The audit of {run.Origin} was blocked by its robots.txt" : $"The audit of {run.Origin} failed",
                error, "audit-ended", cancellationToken);
    }

    /// <summary>
    /// Ends a stage in the caller's unit of work: its findings were written in the same session; this adds the stage to
    /// <c>StagesDone</c> and enqueues the aggregate, which runs once every stage before it is done (§4.3).
    /// </summary>
    public async Task FinishStageAsync(IAsyncDocumentSession session, IMessageOutbox outbox, string runId, string stage, CancellationToken cancellationToken)
    {
        session.Advanced.Patch<AuditRun, string>(runId, r => r.StagesDone, stages => stages.Add(stage));
        await outbox.EnqueueAsync(session, new SeoTester.Messaging.AggregateMessage(runId), cancellationToken: cancellationToken);
    }

    /// <summary>Adds findings to stored pages (by page id) and site-level findings to the frontier, in the caller's unit of work.</summary>
    public static void AddFindings(IAsyncDocumentSession session, string runId, IReadOnlyDictionary<string, List<PageFinding>> pages, IReadOnlyList<StoredFinding> site)
    {
        foreach (var (pageId, findings) in pages)
            if (findings.Count > 0)
            {
                var added = findings.ToArray();
                session.Advanced.Patch<PageResult, PageFinding>(pageId, p => p.Findings, list => list.Add(added));
            }
        if (site.Count > 0)
        {
            var added = site.ToArray();
            session.Advanced.Patch<CrawlFrontier, StoredFinding>(CrawlFrontier.IdFor(runId), f => f.SiteFindings, list => list.Add(added));
        }
    }

    /// <summary>The run's page in the SPA: <c>/po/{alias}/{id}</c>, the alias of <c>AuditRun.json</c> (the SPA resolves a type by alias or id).</summary>
    public static string RunUrl(string runId) => "/po/audit-run/" + Uri.EscapeDataString(runId);

    public async Task NotifyAsync(AuditRun run, string title, string? body, string category, CancellationToken cancellationToken)
    {
        try
        {
            await notifier.NotifyAsync(new SparkNotification
            {
                UserIds = run.Participants.Count > 0 ? run.Participants : run.StartedBy is { } by ? [by] : [],
                Title = title.Length > 200 ? title[..200] : title,
                Body = body,
                Url = RunUrl(run.Id!),
                OrganizationId = run.OrganizationId,
                Category = category,
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The run is over either way; a lost notification must not re-run the stage.
            logger.LogWarning(ex, "Notifying the participants of {RunId} failed", run.Id);
        }
    }

    /// <summary>
    /// Runs one stage of <paramref name="run"/>. An exception is retried by Messaging (the stage is idempotent); on the
    /// queue's last attempt the run is marked failed first, so it never stays "running" forever (and joinable).
    /// </summary>
    public async Task GuardAsync(AuditRun run, IMessageContext context, Func<Task> stage, CancellationToken cancellationToken)
    {
        try
        {
            await stage();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var options = messaging.Value;
            var maxAttempts = options.QueueOptionsFor(context.QueueName)?.MaxAttempts ?? options.MaxAttempts;
            if (context.AttemptCount < maxAttempts)
                throw;
            logger.LogError(ex, "Stage {Queue} of {RunId} failed for the last time", context.QueueName, run.Id);
            await EndAsync(run, AuditRunStatus.Failed, "The audit stopped because of an internal error.", null, CancellationToken.None);
        }
    }
}
