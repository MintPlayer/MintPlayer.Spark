using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Messaging.Models;

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

    [Inject] private readonly ISparkStore store;
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
                // already due, and the store may still be starting.
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
        var now = DateTime.UtcNow;

        // Pending-with-null-NextAttempt is excluded on purpose: those are new messages the
        // subscription already receives on the write that created them.
        // WakeUp != true, not WakeUp == false: an absent JSON field does not match `== false` in
        // RQL, so messages written before the field existed would never be selected. The guard
        // stops the sweep re-writing the same already-woken set every interval — a message stays
        // due until a worker picks it up and clears the gate, so without it each sweep rewrites
        // every parked message, bumping versions and re-triggering delivery for no reason.
        var dueIds = await CandidateIdsAsync(m => (m.Status == EMessageStatus.Pending || m.Status == EMessageStatus.Failed)
                        && m.NextAttemptAtUtc != null
                        && m.NextAttemptAtUtc <= now
                        && m.WakeUp != true, cancellationToken);

        // Written only while the document itself still matches (UpdateWhileStillMatchingAsync): the ids came from a
        // query that may be stale. Under load RavenDB's index lagged by seconds, so it still listed a message as
        // deferred after the feeder woke, claimed and completed it. An unconditional write then rewrote that completed
        // message on every sweep until the index caught up — 11 extra writes per message in one loaded S-M3 run
        // (revision history in contributions_PRD §5c), each one re-dirtying the index it was waiting for — and could
        // set WakeUp on a message Processing or parked at Failed for a later backoff.
        return await UpdateWhileStillMatchingAsync(dueIds,
            m => (m.Status == EMessageStatus.Pending || m.Status == EMessageStatus.Failed)
                 && m.NextAttemptAtUtc != null && m.NextAttemptAtUtc <= now && m.WakeUp != true,
            m =>
            {
                m.WakeUp = true;
                m.LastWakeUpUtc = now;
            },
            cancellationToken);
    }

    private async Task<IReadOnlyList<string>> CandidateIdsAsync(System.Linq.Expressions.Expression<Func<SparkMessage, bool>> predicate, CancellationToken cancellationToken)
    {
        using var session = store.OpenSession();
        var ids = await session.ToListAsync(session.Query<SparkMessage>().Where(predicate).Select(m => m.Id).Take(MaxMessagesPerSweep), cancellationToken);
        return [.. ids.OfType<string>()];
    }

    /// <summary>
    /// Applies <paramref name="change"/> to each of <paramref name="ids"/> that still satisfies
    /// <paramref name="stillMatches"/> as it is stored now, and returns how many it changed (#536 D40). Each save names
    /// the version it read: a document written in between is left alone, and the next sweep looks at it again. Never a
    /// blind full-document write — with last-write-wins it could resurrect a message a worker completed after the read.
    /// It replaces RavenDB patch scripts, so it runs on every store.
    /// </summary>
    private async Task<int> UpdateWhileStillMatchingAsync(
        IReadOnlyList<string> ids, Func<SparkMessage, bool> stillMatches, Action<SparkMessage> change, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return 0;

        IReadOnlyDictionary<string, SparkMessage?> loaded;
        var versions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using (var read = store.OpenSession())
        {
            loaded = await read.LoadAsync<SparkMessage>(ids, cancellationToken);
            foreach (var (id, message) in loaded)
                if (message is not null)
                    versions[id] = read.GetVersion(message);
        }

        var changed = 0;
        foreach (var (id, message) in loaded)
        {
            if (message is null || !stillMatches(message))
                continue;

            change(message);
            using var write = store.OpenSession();
            write.OptimisticConcurrency = true;
            try
            {
                await write.StoreAsync(message, versions[id], id, cancellationToken);
                await write.SaveChangesAsync(cancellationToken);
                changed++;
            }
            catch (SparkStoreConcurrencyException)
            {
                // Written by someone else since the read: it no longer reads as this sweep saw it.
            }
        }
        return changed;
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
        var abandonedIds = await CandidateIdsAsync(m => m.Status == EMessageStatus.Processing
                        && ((m.ClaimExpiresAtUtc != null && m.ClaimExpiresAtUtc <= now)
                            || m.ClaimExpiresAtUtc == null), cancellationToken);

        // A versioned write for the same reason as the wake-up sweep: a blind save would overwrite the
        // whole document under last-write-wins and could resurrect a message the owning process completed
        // after this query ran. It is safe even against a live owner — the owner loses the claim, and the
        // pump's next renewal fails and abandons the message rather than double-completing it.
        //
        // ReclaimCount is incremented so an abandoned message cannot cycle for ever: every reclaim
        // is a failed attempt for each handler that had not finished (RetryBudget), and once the
        // budget is spent MessageProcessor dead-letters the message (MaxAttempts) before any handler
        // runs again, rather than crash-looping the next process that picks it up. P18: this comment
        // used to promise that of AttemptCount, which counts pickups and which the dead-letter
        // decision never read, so a message that killed its host every time was never dead-lettered.
        // AttemptCount is still incremented here, as the pickup count it is.
        //
        // The predicate is re-checked on the document, as in the wake-up: a stale query can still
        // list as Processing a message that has since completed, and an unconditional write would
        // put that completed message back to Pending and run its handlers a second time.
        return await UpdateWhileStillMatchingAsync(abandonedIds,
            m => m.Status == EMessageStatus.Processing && (m.ClaimExpiresAtUtc == null || m.ClaimExpiresAtUtc <= now),
            m =>
            {
                m.Status = EMessageStatus.Pending;
                m.OwnerId = null;
                m.ClaimExpiresAtUtc = null;
                m.WakeUp = true;
                m.LastWakeUpUtc = now;
                m.AttemptCount++;
                m.ReclaimCount++;
            },
            cancellationToken);
    }
}
