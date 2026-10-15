using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Commands.Batches;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Session;

namespace CodeCoverage.MergeQueue;

// ── The checks a repository has seen (merge-queue PRD §11, O38, D107–D109) ─────────────────────────────────────────
//
// Three sources feed one writer: the check webhooks (CheckReported, raised by each forge's normaliser), the queue's own
// check reads (the same event, through the engine's outbox), and a backfill over the default branch and the open pull
// requests (RefreshKnownChecksMessage). Each source already skips names that are known and fresh, so the writer is
// reached once per name per day at most, and re-checks against the stored list before it writes.

public interface IKnownChecksRecorder
{
    /// <summary>
    /// Adds the checks in <paramref name="reported"/> that are new to the repository, or refreshes those a day stale
    /// (<see cref="ForgeCheck.Due"/>), keeping the newest <see cref="ForgeCheck.MaxKnown"/>. Writes nothing when nothing is
    /// due, or when the repository does not exist.
    /// </summary>
    Task RecordAsync(string repositoryId, IEnumerable<ForgeCheck> reported, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IKnownChecksRecorder"/>
/// <remarks>
/// <para>
/// ⚠️ <b>A patch, never a save of the loaded repository.</b> Repository is rewritten by every upload (its headline
/// coverage) and by every reconcile; a load-modify-save here would put back whatever those wrote in between. The patch
/// touches <see cref="Repository.KnownChecks"/> alone and merges on the server, so two recorders racing on one
/// repository both land.
/// </para>
/// <para>
/// The script is a constant and every value travels in its arguments: nothing from a forge (a check name is anybody's
/// text) is ever part of the script.
/// </para>
/// </remarks>
[Register(typeof(IKnownChecksRecorder), ServiceLifetime.Scoped)]
public partial class KnownChecksRecorder : IKnownChecksRecorder
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly TimeProvider time;

    /// <summary>
    /// Upserts <c>args.checks</c> by <c>Id</c>: a later <c>LastSeenUtc</c> wins, a known kind or App is never cleared, and
    /// beyond <c>args.max</c> the oldest <c>LastSeenUtc</c> goes. Dates are ISO-8601 round-trip strings, which compare in
    /// time order as text.
    /// </summary>
    internal const string UpsertScript = """
        var list = this.KnownChecks || [];
        for (var i = 0; i < args.checks.length; i++) {
            var seen = args.checks[i];
            var found = null;
            for (var j = 0; j < list.length; j++) {
                if (list[j].Id === seen.Id) { found = list[j]; break; }
            }
            if (found === null) {
                list.push(seen);
                continue;
            }
            if (!found.LastSeenUtc || seen.LastSeenUtc > found.LastSeenUtc) found.LastSeenUtc = seen.LastSeenUtc;
            if (seen.Kind !== null && seen.Kind !== undefined) found.Kind = seen.Kind;
            if (seen.AppSlug !== null && seen.AppSlug !== undefined) found.AppSlug = seen.AppSlug;
            found.Name = seen.Name;
        }
        if (list.length > args.max) {
            list.sort(function (a, b) { return a.LastSeenUtc < b.LastSeenUtc ? 1 : a.LastSeenUtc > b.LastSeenUtc ? -1 : 0; });
            list = list.slice(0, args.max);
        }
        this.KnownChecks = list;
        """;

    public async Task RecordAsync(string repositoryId, IEnumerable<ForgeCheck> reported, CancellationToken cancellationToken = default)
    {
        var repository = await session.LoadAsync<Repository>(repositoryId, cancellationToken);
        if (repository is null) return;

        var due = ForgeCheck.Due(repository.KnownChecks, reported, time.GetUtcNow().UtcDateTime);
        if (due.Count == 0) return;

        // The patch must be this session's only write to the repository. A document saved before KnownChecks existed
        // loads with an empty list, which the session counts as a change, and Raven refuses a deferred patch on a
        // document it would also save. Evicting also keeps a caller's earlier load of it (the refresh) from being saved.
        session.Advanced.Evict(repository);
        session.Advanced.Defer(new PatchCommandData(repositoryId, null, new PatchRequest
        {
            Script = UpsertScript,
            Values =
            {
                ["checks"] = due,
                ["max"] = ForgeCheck.MaxKnown,
            },
        }));
        await session.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Writes the check a forge reported down as one the exclusion lists may pick (§11 D108 (a), (b)).</summary>
public partial class KnownChecksRecipient : IRecipient<ForgeWebhookMessage<CheckReported>>
{
    [Inject] private readonly IKnownChecksRecorder recorder;

    public Task HandleAsync(ForgeWebhookMessage<CheckReported> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        return recorder.RecordAsync(evt.RepositoryId, [new ForgeCheck { Name = evt.Name, Kind = evt.Kind, AppSlug = evt.AppSlug }], cancellationToken);
    }
}

/// <summary>
/// Reads the checks that reported on one repository's default branch and open pull requests into
/// <see cref="Repository.KnownChecks"/> (merge-queue PRD §11, D109); handled by <see cref="RefreshKnownChecksRecipient"/>.
/// </summary>
/// <remarks>
/// On <see cref="CoverageQueues.Publishing"/> because it calls the forge, like <c>ScanRepositoryManifestsMessage</c>.
/// Always broadcast with <see cref="DailyKey"/>, so a second reconcile on the same day queues nothing.
/// </remarks>
[MessageQueue(CoverageQueues.Publishing)]
public sealed record RefreshKnownChecksMessage
{
    /// <summary>The repository document id, e.g. <c>Repositories/github/123</c>.</summary>
    public required string RepositoryId { get; init; }

    /// <summary>One backfill per repository per UTC day.</summary>
    public static string DailyKey(string repositoryId, DateTimeOffset now) => $"known-checks-{repositoryId}-{now.UtcDateTime:yyyy-MM-dd}";
}

/// <summary>
/// The backfill (§11 D109): one forge read per repository, through <see cref="IForgeMergeQueue.GetReportedCheckNamesAsync"/>.
/// A repository whose forge has no merge queue, or that is disconnected or archived, is skipped; so is one the forge
/// refuses, which the next nightly reconcile tries again.
/// </summary>
public partial class RefreshKnownChecksRecipient : IRecipient<RefreshKnownChecksMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> forgeQueues;
    [Inject] private readonly IKnownChecksRecorder recorder;
    [Inject] private readonly ILogger<RefreshKnownChecksRecipient> logger;

    public async Task HandleAsync(RefreshKnownChecksMessage message, CancellationToken cancellationToken = default)
    {
        var repository = await session.LoadAsync<Repository>(message.RepositoryId, cancellationToken);
        if (repository is null || repository.Connection == RepositoryConnection.Disconnected || repository.Archived) return;
        if (forgeQueues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } queue) return;

        IReadOnlyList<ForgeReportedCheck> reported;
        try
        {
            reported = await queue.GetReportedCheckNamesAsync(repository, cancellationToken);
        }
        catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException)
        {
            // Not worth a redelivery: the webhooks and the queue's own reads keep the list growing meanwhile.
            logger.LogWarning(ex, "Could not read the reported checks of {Repository}", repository.FullName);
            return;
        }

        await recorder.RecordAsync(repository.Id!,
            reported.Select(check => new ForgeCheck { Name = check.Name, Kind = check.Kind, AppSlug = check.AppSlug }),
            cancellationToken);
    }
}

public interface IKnownChecksScheduler
{
    /// <summary>
    /// Queues <see cref="RefreshKnownChecksMessage"/> for every connected, unarchived repository of
    /// <paramref name="account"/>, at most once per repository per UTC day. Never throws: a failure here must not undo the
    /// reconcile that called it.
    /// </summary>
    /// <param name="waitForIndex">True right after the caller saved repositories it may have just created.</param>
    Task ScheduleAccountAsync(Account account, bool waitForIndex, CancellationToken cancellationToken = default);
}

/// <summary>
/// The scheduled half of the backfill (§11 D109): after every account reconcile, the nightly sweep and the one an
/// installation change queues. The same per-repository fan-out as <c>ManifestScanScheduler</c>, kept apart from it
/// because the two features have nothing else in common.
/// </summary>
[Register(typeof(IKnownChecksScheduler), ServiceLifetime.Scoped)]
public partial class KnownChecksScheduler : IKnownChecksScheduler
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly ILogger<KnownChecksScheduler> logger;

    public async Task ScheduleAccountAsync(Account account, bool waitForIndex, CancellationToken cancellationToken = default)
    {
        try
        {
            var ownerKey = account.OwnerKey;
            var query = session.Query<Repository, Indexes.Repositories_Overview>();
            if (waitForIndex)
                query = query.Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(15)));

            var repositories = await query
                .Where(r => r.OwnerKey == ownerKey)
                .Take(1024)
                .ToListAsync(cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var queued = 0;
            foreach (var repository in repositories)
            {
                if (repository.Connection == RepositoryConnection.Disconnected || repository.Archived) continue;

                await messageBus.BroadcastOnceAsync(
                    new RefreshKnownChecksMessage { RepositoryId = repository.Id! },
                    RefreshKnownChecksMessage.DailyKey(repository.Id!, now),
                    cancellationToken);
                queued++;
            }

            logger.LogDebug("Queued known-check refreshes for {Count} repositories of {Login}", queued, account.Login);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The nightly sweep tries again tomorrow; the reconcile that called this has already saved.
            logger.LogWarning(ex, "Could not queue known-check refreshes for {Login}", account.Login);
        }
    }
}
