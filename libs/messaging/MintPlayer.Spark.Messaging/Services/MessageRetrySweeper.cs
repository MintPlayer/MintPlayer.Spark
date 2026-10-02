using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Indexes;
using MintPlayer.Spark.Messaging.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Commands.Batches;
using Raven.Client.Documents.Operations;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// The wake-up for parked messages (issue #233). RavenDB subscriptions are
/// change-vector-driven: a document is re-evaluated against the subscription query only
/// when it is written — time passing re-evaluates nothing, and a where-clause cannot
/// evaluate time either (<c>NextAttemptAtUtc &lt;= now()</c> silently never matches). A
/// message parked at <c>Failed</c> (retry backoff) or <c>Pending</c> with a future
/// <c>NextAttemptAtUtc</c> (delayed broadcast) is therefore invisible to
/// <see cref="MessageSubscriptionWorker"/> until this service acts. Every
/// <see cref="SparkMessagingOptions.FallbackPollInterval"/> it patches
/// <see cref="SparkMessage.WakeUp"/> to true on due messages — materializing "the backoff
/// has elapsed" as plain field state the subscription query can match, while the patch
/// itself bumps the change vector that triggers re-evaluation. Redelivery granularity is
/// therefore <see cref="SparkMessagingOptions.FallbackPollInterval"/> (default 30s).
/// </summary>
internal sealed partial class MessageRetrySweeper : BackgroundService
{
    // Bounds a single sweep; a larger backlog drains over subsequent sweeps.
    private const int MaxMessagesPerSweep = 512;

    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkMessagingOptions> options;
    [Inject] private readonly ILogger<MessageRetrySweeper> logger;
    [Inject] private readonly MessagingLeaseManager leaseManager;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Delay first: at startup the subscriptions themselves deliver everything
                // already due, and the SparkMessages_ByQueue index may still be deploying.
                await Task.Delay(options.Value.FallbackPollInterval, stoppingToken);

                // Leader-only. Every write below is idempotent, so this is a courtesy rather than
                // a safety gate — but without it every standby host patches the same due messages
                // on the same interval, multiplying writes and change-vector churn by the replica
                // count for no benefit.
                if (!leaseManager.IsHeld)
                    continue;

                var touched = await SweepOnceAsync(stoppingToken);
                if (touched > 0)
                    logger.LogInformation("Woke up {Count} due message(s) for redelivery", touched);

                var reclaimed = await ReclaimAbandonedAsync(stoppingToken);
                if (reclaimed > 0)
                    logger.LogWarning(
                        "Reclaimed {Count} message(s) abandoned at Processing by a host that stopped mid-handler",
                        reclaimed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Message retry sweep failed; retrying next interval");
            }
        }
    }

    /// <summary>Touches every due parked message once. Internal for tests.</summary>
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        var now = DateTime.UtcNow;

        // Pending-with-null-NextAttempt is excluded on purpose: those are new messages the
        // subscription already receives on the write that created them.
        // WakeUp != true, not WakeUp == false: an absent JSON field does not match `== false` in
        // RQL, so messages written before the field existed would never be selected. The guard
        // stops the sweep re-patching the same already-woken set every interval — a message stays
        // due until a worker picks it up and clears the gate, so without it each sweep rewrites
        // every parked message, bumping change vectors and re-triggering delivery for no reason.
        // MessageRetrySweeper's replication twin has had this guard; this one did not.
        var dueIds = await session.Query<SparkMessage, SparkMessages_ByQueue>()
            .Where(m => (m.Status == EMessageStatus.Pending || m.Status == EMessageStatus.Failed)
                        && m.NextAttemptAtUtc != null
                        && m.NextAttemptAtUtc <= now
                        && m.WakeUp != true)
            .Select(m => m.Id)
            .Take(MaxMessagesPerSweep)
            .ToListAsync(cancellationToken);

        if (dueIds.Count == 0)
            return 0;

        // Server-side patches, never load-modify-save: with last-write-wins a full-document save
        // could resurrect a message the worker completed after our query ran. And the patch
        // re-checks the query's predicate on the document as it is NOW, because the ids came from
        // an index that may be stale: under load SparkMessages_ByQueue lags by seconds, so it still
        // lists a message as deferred after the feeder woke, claimed and completed it. An
        // unconditional patch then rewrote that completed message on every sweep until the index
        // caught up — 11 extra writes per message in one loaded S-M3 run (revision history in
        // contributions_PRD §5c), each one re-dirtying the index it was waiting for — and could set
        // WakeUp on a message Processing or parked at Failed for a later backoff. A document that no
        // longer matches is left untouched: a no-op patch writes nothing.
        return await PatchWhereStillMatchingAsync(dueIds!, WakeUpScript, now, cancellationToken);
    }

    /// <summary>The wake-up, applied only while the document itself is still due and unwoken.</summary>
    private const string WakeUpScript = """
        if ((this.Status === 'Pending' || this.Status === 'Failed')
            && this.NextAttemptAtUtc != null && this.NextAttemptAtUtc <= args.now
            && this.WakeUp !== true) {
            this.WakeUp = true;
            this.LastWakeUpUtc = args.now;
        }
        """;

    /// <summary>The reclaim, applied only while the document itself is still at Processing on a lapsed (or absent) claim.</summary>
    private const string ReclaimScript = """
        if (this.Status === 'Processing'
            && (this.ClaimExpiresAtUtc == null || this.ClaimExpiresAtUtc <= args.now)) {
            this.Status = 'Pending';
            this.OwnerId = null;
            this.ClaimExpiresAtUtc = null;
            this.WakeUp = true;
            this.LastWakeUpUtc = args.now;
            this.AttemptCount = (this.AttemptCount || 0) + 1;
        }
        """;

    /// <summary>
    /// Runs <paramref name="script"/> on every id in one batch and returns how many documents it
    /// actually changed. The script compares <c>args.now</c> with the stored dates as strings, which is
    /// chronological because RavenDB writes a UTC <see cref="DateTime"/> in this same fixed-width form.
    /// </summary>
    private async Task<int> PatchWhereStillMatchingAsync(IReadOnlyList<string> ids, string script, DateTime now, CancellationToken cancellationToken)
    {
        var patch = new PatchRequest
        {
            Script = script,
            Values = { ["now"] = now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture) },
        };
        var commands = ids.Select(id => (ICommandData)new PatchCommandData(id, changeVector: null, patch)).ToList();

        var executor = documentStore.GetRequestExecutor();
        using (executor.ContextPool.AllocateOperationContext(out var context))
        {
            var command = new SingleNodeBatchCommand(documentStore.Conventions, commands);
            await executor.ExecuteAsync(command, context, sessionInfo: null, cancellationToken);

            var patched = 0;
            foreach (var item in command.Result!.Results)
            {
                if (item is Sparrow.Json.BlittableJsonReaderObject result
                    && result.TryGet(nameof(PatchStatus), out string? status)
                    && status == nameof(PatchStatus.Patched))
                    patched++;
            }
            return patched;
        }
    }

    /// <summary>
    /// Returns messages abandoned at <see cref="EMessageStatus.Processing"/> to
    /// <see cref="EMessageStatus.Pending"/> so they are delivered again. Internal for tests.
    /// <para>
    /// This is the reader that <c>Processing</c> never had. The status was written on pickup and
    /// consulted by nothing — not the subscription query, not this sweeper — so a host that died
    /// between pickup and completion stranded the message permanently: it matched no query, no
    /// retry applied, nothing was logged, and the queue simply lost the work. For webhook traffic
    /// that meant an accepted delivery was dropped, and GitHub does not re-deliver on its own.
    /// </para>
    /// <para>
    /// A claim lapses by wall clock, so the TTL must exceed the slowest handler
    /// (<see cref="SparkMessagingOptions.ClaimTtl"/>, renewed while a handler runs). Reclaiming a
    /// message that is still being processed causes duplicate work, which is worse than reclaiming
    /// it late.
    /// </para>
    /// </summary>
    internal async Task<int> ReclaimAbandonedAsync(CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        var now = DateTime.UtcNow;

        // Two arms, and the second is the upgrade path.
        //
        // A claim that has lapsed is the ordinary case. But messages stranded at Processing by a
        // build from *before* claims existed have no ClaimExpiresAtUtc at all, so an
        // `ClaimExpiresAtUtc <= now` test can never match them and they would stay stranded for
        // ever — the very bug this method exists to fix, surviving the fix. Any Processing document
        // with no claim expiry was necessarily written by a build that could not set one, because
        // every claim taken now sets it, so it is abandoned by definition.
        //
        // `== null` is correct here where `!= true` is needed for booleans: a missing field does not
        // match `== false`, but it does match `== null`.
        var abandonedIds = await session.Query<SparkMessage, SparkMessages_ByQueue>()
            .Where(m => m.Status == EMessageStatus.Processing
                        && ((m.ClaimExpiresAtUtc != null && m.ClaimExpiresAtUtc <= now)
                            || m.ClaimExpiresAtUtc == null))
            .Select(m => m.Id)
            .Take(MaxMessagesPerSweep)
            .ToListAsync(cancellationToken);

        if (abandonedIds.Count == 0)
            return 0;

        // Field-level patches for the same reason as the wake-up sweep: a load-modify-save would
        // overwrite the whole document under last-write-wins and could resurrect a message the
        // owning process completed after this query ran. Patching only the claim fields is safe
        // even against a live owner — it loses the claim, and the pump's next renewal fails and
        // abandons the message rather than double-completing it.
        //
        // AttemptCount is incremented so an abandoned message cannot cycle for ever: a message
        // that reliably kills its host is retried, backed off and eventually dead-lettered like
        // any other failure, rather than crash-looping the process that picks it up.
        //
        // The predicate is re-checked on the document, as in the wake-up: a stale index can still
        // list as Processing a message that has since completed, and an unconditional patch would
        // put that completed message back to Pending and run its handlers a second time.
        return await PatchWhereStillMatchingAsync(abandonedIds!, ReclaimScript, now, cancellationToken);
    }
}
