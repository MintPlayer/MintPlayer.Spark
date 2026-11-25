using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.SubscriptionWorker;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// The single subscription behind all Spark messaging. On each batch it claims the top
/// of the sorted claimable set together with what was delivered (#460 M16b, see
/// <see cref="ServeOrder"/>) and hands each to <see cref="MessageQueueRouter"/>; it runs no handler and makes no
/// outbound call, so nothing a recipient does can stall delivery for other queues.
/// <para>
/// <b>Why one subscription.</b> Spark previously created one per distinct queue name. RavenDB caps
/// data subscriptions per database — 3 on a Community licence — so the number of queues an
/// application could have was decided by its licence, and exceeding the cap failed quietly: the
/// create was refused, the worker started against a subscription that did not exist, died as
/// "non-recoverable", and the process stayed up looking healthy with a dead queue. Seven
/// definitions had accumulated in one production database and five of them were doing nothing.
/// Queue names are a modelling decision, and this makes them free again.
/// </para>
/// </summary>
internal sealed class MessageFeeder : SparkSubscriptionWorker<SparkMessage>
{
    /// <summary>
    /// <b>Deliberately has no trailing hyphen.</b> <see cref="LegacySubscriptionCleanup"/> deletes
    /// every subscription whose name starts with <c>"SparkMessaging-"</c>, and it runs on every
    /// boot so that a stale definition can never accumulate again. This name escapes that prefix by
    /// exactly one character. Rename it to something like <c>SparkMessaging-Unified</c> and the
    /// cleanup will delete its own live subscription on every startup — which, unlike most faults
    /// here, does not heal on restart.
    /// </summary>
    public const string SubscriptionNameConstant = "SparkMessaging";

    private readonly MessageQueueRouter router;
    private readonly QueueAdmission admission;
    private readonly SparkMessagingOptions options;
    private readonly ISparkStore store;

    protected override string SubscriptionName => SubscriptionNameConstant;

    /// <summary>
    /// Both the subscription batch and the sorted page (#460, M16/M16b). One document per batch is what
    /// made a bulk backlog delay urgent mail in M4 (S-M3: up to 5.8 s behind 1,000 bulk messages); a
    /// batch is now claimed a priority at a time, with one load and one write per priority, and a
    /// message its throttled queue cannot start yet is deferred right here instead of being claimed.
    /// </summary>
    protected override int MaxDocsPerBatch => PageSize;

    private int PageSize => Math.Clamp(options.FeederBatchSize, 1, 4096);

    /// <summary>Messages served from the sorted page that the subscription batch did not carry. For the spike and diagnostics.</summary>
    internal long PulledAheadCount;

    /// <summary>Messages served from the batch that the sorted page did not contain (not visible to the page query yet, or beyond the page). For the spike and diagnostics.</summary>
    internal long BatchOnlyCount;

    // Hand-written ctor: this worker is constructed by MessageSubscriptionManager rather than
    // resolved from DI, because it only exists while this host holds the messaging lease.
    public MessageFeeder(
        MessageQueueRouter router,
        QueueAdmission admission,
        ISparkSubscriptions subscriptions,
        ISparkStore store,
        IOptions<SparkMessagingOptions> options,
        ILoggerFactory loggerFactory)
        : base(subscriptions, loggerFactory)
    {
        this.store = store;
        this.router = router;
        this.admission = admission;
        this.options = options.Value;
    }

    /// <summary>A message the feeder may serve on this wake-up, with its sort key.</summary>
    internal readonly record struct Candidate(string Id, string QueueName, int Priority, DateTime Sequence, bool Expedite = false);

    /// <summary>
    /// The order in which one wake-up is served (#460, M16b): the sorted page and the subscription
    /// batch merged, each message once, in priority groups, highest first, each group by
    /// <c>Sequence</c> (then id, for ties). Two properties follow and are what the tests pin:
    /// <list type="bullet">
    /// <item><b>Strict priority beyond the batch.</b> The page is the top of the whole claimable set
    /// by <c>Priority desc, Sequence asc</c>, so a High message enqueued behind any backlog is served
    /// on the next wake-up, ahead of every Low one.</item>
    /// <item><b>No starvation.</b> Every message the subscription delivered is in the result, whatever
    /// the page holds. The subscription walks the queue in commit order and each message is claimed or
    /// deferred in the batch that delivers it, so a Low message waits at most for the subscription to
    /// reach it, plus one page per batch pulled ahead of it: never indefinitely, and no aging is needed.
    /// A message the page query does not see yet is served from the batch the same way.</item>
    /// </list>
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<Candidate>> ServeOrder(IEnumerable<Candidate> page, IEnumerable<Candidate> batch)
    {
        var merged = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        foreach (var candidate in page.Concat(batch))
            merged.TryAdd(candidate.Id, candidate);

        return merged.Values
            .OrderByDescending(c => c.Priority)
            // R29: within a queue priority, expedited messages first. The lane reorders again on its
            // own (it may already hold normal messages); this decides which are claimed first.
            .ThenByDescending(c => c.Expedite)
            .ThenBy(c => c.Sequence)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .GroupBy(c => c.Priority)
            .Select(g => (IReadOnlyList<Candidate>)g.ToList())
            .ToList();
    }

    /// <summary>The subscription's predicate, re-checked on the loaded document: the page may be stale.</summary>
    internal static bool IsClaimableNow(SparkMessage message)
        => (message.Status == EMessageStatus.Pending && (message.NextAttemptAtUtc is null || message.WakeUp))
            || (message.Status == EMessageStatus.Failed && message.WakeUp);

    /// <summary>
    /// The claimable predicate the subscription and the page share. No QueueName predicate — that is the whole point.
    /// It also retires the injection surface that QueueNames.IsValid existed to guard: with no queue name in the
    /// filter, a hostile queue name has nothing to break out of.
    /// <para>
    /// Note WakeUp where a time comparison would be natural: `NextAttemptAtUtc &lt;= now()` in a subscription
    /// filter silently never matches on RavenDB, so "the backoff has elapsed" is materialized as plain field state
    /// by MessageRetrySweeper, which can evaluate time. Never put the clock here (invariant 7).
    /// </para>
    /// </summary>
    internal static readonly Expression<Func<SparkMessage, bool>> Claimable =
        m => (m.Status == EMessageStatus.Pending && (m.NextAttemptAtUtc == null || m.WakeUp == true))
            || (m.Status == EMessageStatus.Failed && m.WakeUp == true);

    protected override Expression<Func<SparkMessage, bool>> Filter => Claimable;

    /// <summary>
    /// The sort keys of one wake-up (#536 D40). A message the feeder already wrote keeps the
    /// <see cref="SparkMessage.QueuedAtUtc"/> it captured then. A message it never wrote gets one now, on the lease
    /// holder's clock — the single writer of every <c>QueuedAtUtc</c>, so never the producer's clock — after every key
    /// already handed out: first the batch in delivery order, which is commit order on every store, then what only
    /// the page holds, by <see cref="SparkMessage.CreatedAtUtc"/>. The claim or deferral write stores it, so a retry,
    /// a reclaim or a throttle wake-up keeps the place.
    /// <para>
    /// This replaces RavenDB's <c>@last-modified</c> (S-M7), which no other store has. It reads no metadata, so
    /// the page is a plain query on every store instead of a RavenDB index.
    /// </para>
    /// </summary>
    internal static (IReadOnlyList<Candidate> Page, IReadOnlyList<Candidate> Batch) AssignSequences(
        IReadOnlyList<SparkMessage> page, IReadOnlyList<SparkMessage> batch, DateTime now)
    {
        var assigned = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var next = now;
        foreach (var message in batch.Concat(page.Where(m => m.QueuedAtUtc is null).OrderBy(m => m.CreatedAtUtc)))
        {
            if (message.Id is null || message.QueuedAtUtc is not null || assigned.ContainsKey(message.Id))
                continue;
            assigned[message.Id] = next;
            next = next.AddTicks(1);
        }

        Candidate ToCandidate(SparkMessage m) => new(m.Id!, m.QueueName, m.Priority, m.QueuedAtUtc ?? assigned[m.Id!], m.Expedite);
        return ([.. page.Where(m => m.Id is not null).Select(ToCandidate)], [.. batch.Where(m => m.Id is not null).Select(ToCandidate)]);
    }

    /// <summary>
    /// One wake-up (#460, M16b). The subscription stays the wake-up signal and the backstop; what is
    /// served is the sorted page of every claimable message merged with the batch — see
    /// <see cref="ServeOrder"/>.
    /// </summary>
    protected override async Task ProcessBatchAsync(SparkSubscriptionBatch<SparkMessage> batch, CancellationToken cancellationToken)
    {
        var (page, delivered) = AssignSequences(
            await ReadPageAsync(cancellationToken),
            [.. batch.Items.Select(i => i.Document)],
            DateTime.UtcNow);

        var inPage = page.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var inBatch = delivered.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        Interlocked.Add(ref PulledAheadCount, inPage.Count(id => !inBatch.Contains(id)));
        Interlocked.Add(ref BatchOnlyCount, inBatch.Count(id => !inPage.Contains(id)));

        // Store sessions, never one tied to the batch: RavenDB allows ONE session per subscription batch and throws on
        // the second, and a wake-up opens several (the page, one per priority, the conflict fallback). The batch's own
        // tracking is not needed: everything is re-loaded.
        foreach (var group in ServeOrder(page, delivered))
            await FeedGroupAsync(group, cancellationToken);
    }

    /// <summary>
    /// The top <see cref="SparkMessagingOptions.FeederBatchSize"/> claimable messages by
    /// <c>Priority desc, Expedite desc, QueuedAtUtc asc</c>, read as the store has them: never waiting for a
    /// RavenDB index to catch up, because whatever it does not see yet is in the subscription batch. A message the
    /// feeder never wrote has no <c>QueuedAtUtc</c> and sorts first in its priority (nulls first, D4);
    /// <see cref="AssignSequences"/> then places it after the captured ones.
    /// </summary>
    private async Task<IReadOnlyList<SparkMessage>> ReadPageAsync(CancellationToken cancellationToken)
    {
        using var session = store.OpenSession();
        return await session.ToListAsync(
            session.Query<SparkMessage>()
                .Where(Claimable)
                .OrderByDescending(m => m.Priority)
                .ThenByDescending(m => m.Expedite)
                .ThenBy(m => m.QueuedAtUtc)
                .Take(PageSize),
            cancellationToken);
    }

    /// <summary>
    /// One priority group: one load, the admission decision per message, one write for every claim and
    /// deferral, then routing. A conflict on that write (another feeder, or the sweeper, touched one of
    /// the messages) fails it as a whole, and the group is then fed one message at a time.
    /// </summary>
    private async Task FeedGroupAsync(IReadOnlyList<Candidate> group, CancellationToken cancellationToken)
    {
        var ids = group.Select(c => c.Id).ToArray();
        var sequences = group.ToDictionary(c => c.Id, c => c.Sequence, StringComparer.Ordinal);
        var decisions = new Dictionary<string, AdmissionDecision>(StringComparer.Ordinal);
        var toRoute = new List<SparkMessage>();

        using (var session = store.OpenSession())
        {
            // Without this the claim is last-write-wins, and two feeders would both believe they
            // claimed the same message and both run its handlers.
            session.OptimisticConcurrency = true;
            var loaded = await session.LoadAsync<SparkMessage>(ids, cancellationToken);

            var now = DateTime.UtcNow;
            foreach (var id in ids)
            {
                // The full predicate, not just "not Processing": a page entry can be stale, and a
                // message finished, deferred or parked since the page was read must not be claimed again.
                if (loaded.GetValueOrDefault(id) is not { } tracked || !IsClaimableNow(tracked))
                {
                    Logger.LogDebug("Message {MessageId} is gone, already claimed or no longer due; skipping", id);
                    continue;
                }

                tracked.QueuedAtUtc ??= sequences[id];
                var decision = Decide(tracked, now);
                decisions[id] = decision;
                if (decision.Kind == AdmissionKind.Defer)
                    Defer(tracked, decision.Slot);
                else
                {
                    Claim(tracked);
                    toRoute.Add(tracked);
                }
            }

            if (decisions.Count == 0)
                return;

            try
            {
                await session.SaveChangesAsync(cancellationToken);
            }
            catch (SparkStoreConcurrencyException)
            {
                Logger.LogDebug("A message in a group of {Count} changed while being claimed; claiming them one at a time", decisions.Count);
                toRoute = null;
            }
        }

        if (toRoute is null)
        {
            toRoute = [];
            foreach (var (id, decision) in decisions)
            {
                if (await FeedOneAsync(id, sequences[id], decision, cancellationToken) is { } claimed)
                    toRoute.Add(claimed);
            }
        }

        // Claimed and durably saved BEFORE the batch is acknowledged. If this process dies from here
        // on, the message sits at Processing with an expiring claim and the sweeper returns it to the
        // queue. That ordering is the webhook-drop fix: previously the batch could be acknowledged for
        // a message whose Processing status nothing ever read again.
        for (var i = 0; i < toRoute.Count; i++)
        {
            try
            {
                await router.RouteAsync(toRoute[i].QueueName, toRoute[i].Id!, toRoute[i].Expedite, cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Threading.Channels.ChannelClosedException)
            {
                // Stopping: the subscription's Run() can return while this callback is still running
                // (measured in S-M8), so the lanes may already be closed for the drain. The claims
                // taken above would otherwise sit at Processing until ClaimTtl (5 min by default) on
                // every graceful deploy; released, the next leader serves them at once.
                await ReleaseUnroutedAsync(toRoute.Skip(i));
                throw;
            }
        }
    }

    private async Task ReleaseUnroutedAsync(IEnumerable<SparkMessage> unrouted)
    {
        var count = 0;
        foreach (var message in unrouted)
        {
            admission.Forget(message.Id!);
            try
            {
                await MessageClaims.ReleaseUnstartedAsync(store, message.Id!, MessageClaims.NodeId, CancellationToken.None);
                count++;
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not release message {MessageId}; it is reclaimed once its claim expires", message.Id);
            }
        }
        if (count > 0)
            Logger.LogInformation("Released {Count} claimed message(s) that could not be routed because messaging is stopping", count);
    }

    /// <summary>The fallback for a conflicting group write: the decision already taken, one message at a time.</summary>
    private async Task<SparkMessage?> FeedOneAsync(string id, DateTime sequence, AdmissionDecision decision, CancellationToken cancellationToken)
    {
        using var session = store.OpenSession();
        session.OptimisticConcurrency = true;

        var tracked = await session.LoadAsync<SparkMessage>(id, cancellationToken);

        // Already claimed by someone else, or no longer eligible. The subscription can deliver a
        // document more than once (a reconnect replays an unacknowledged batch), so this is an
        // ordinary occurrence, not an error.
        if (tracked is null || !IsClaimableNow(tracked))
        {
            if (decision.Kind != AdmissionKind.Defer)
                admission.Forget(id);
            return null;
        }

        tracked.QueuedAtUtc ??= sequence;
        if (decision.Kind == AdmissionKind.Defer)
        {
            Defer(tracked, decision.Slot);
            try { await session.SaveChangesAsync(cancellationToken); }
            catch (SparkStoreConcurrencyException) { Logger.LogDebug("Message {MessageId} changed while being deferred; leaving it", id); }
            return null;
        }

        var claimed = await MessageClaims.TryClaimAsync(session, tracked, MessageClaims.NodeId, options.ClaimTtl, cancellationToken);
        if (!claimed)
        {
            Logger.LogDebug("Lost the race to claim message {MessageId}; another host has it", id);
            admission.Forget(id);
            return null;
        }
        return tracked;
    }

    /// <summary>
    /// Throttle admission before the claim. Only a deferral is acted on here; an expired message is
    /// claimed and routed as before, and the processor dead-letters it with the reason.
    /// </summary>
    private AdmissionDecision Decide(SparkMessage message, DateTime now)
    {
        if (message.ExpiresAtUtc is { } expires && expires <= now)
            return AdmissionDecision.Admit;
        var decision = admission.DecideBeforeClaim(message.QueueName, message.Id!, options.QueueOptionsFor(message.QueueName), now, message.ExpiresAtUtc);
        return decision.Kind == AdmissionKind.Expired ? AdmissionDecision.Admit : decision;
    }

    /// <summary>The claim <see cref="MessageClaims.TryClaimAsync"/> writes, without the save.</summary>
    private void Claim(SparkMessage message)
    {
        message.Status = EMessageStatus.Processing;
        message.OwnerId = MessageClaims.NodeId;
        message.ClaimExpiresAtUtc = DateTime.UtcNow + options.ClaimTtl;
        message.AttemptCount++;
        // Consumed on pickup: left true, the message would keep matching the subscription query and
        // be redelivered in a tight loop.
        message.WakeUp = false;
    }

    /// <summary>
    /// The shape <see cref="MessageClaims.DeferUnstartedAsync"/> leaves, written without ever claiming:
    /// Pending, unwoken, due at its reserved slot. Not an attempt, so the count is untouched.
    /// </summary>
    private static void Defer(SparkMessage message, DateTime slot)
    {
        message.Status = EMessageStatus.Pending;
        message.OwnerId = null;
        message.ClaimExpiresAtUtc = null;
        message.WakeUp = false;
        message.NextAttemptAtUtc = slot;
    }
}
