using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.SubscriptionWorker;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Subscriptions;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// The single RavenDB data subscription behind all Spark messaging. It claims each delivered
/// message and hands it to <see cref="MessageQueueRouter"/>; it runs no handler and makes no
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

    protected override string SubscriptionName => SubscriptionNameConstant;

    /// <summary>
    /// The look-ahead window (#460, M16). It was one document per batch, and that is what made a
    /// bulk backlog delay urgent mail (S-M3: a transactional message waited up to 5.8 s behind 1,000
    /// bulk messages): the subscription delivers in etag order, and every message in front cost a
    /// load and a claim write of its own, then a second write in its lane when the throttle deferred
    /// it. A window is claimed a priority at a time, with one load and one write per priority, and a
    /// message its throttled queue cannot start yet is deferred right here instead of being claimed.
    /// </summary>
    protected override int MaxDocsPerBatch => Math.Clamp(options.FeederBatchSize, 1, 4096);

    // Hand-written ctor: this worker is constructed by MessageSubscriptionManager rather than
    // resolved from DI, because it only exists while this host holds the messaging lease.
    public MessageFeeder(
        MessageQueueRouter router,
        QueueAdmission admission,
        IDocumentStore store,
        IOptions<SparkMessagingOptions> options,
        ILoggerFactory loggerFactory)
        : base(loggerFactory, store)
    {
        this.router = router;
        this.admission = admission;
        this.options = options.Value;
    }

    /// <summary>
    /// The order in which one window is served: priority groups, highest first, each in delivery
    /// (etag) order. Every message of the window is in exactly one group — a lower priority is served
    /// later in the window, never dropped from it — which is the no-starvation bound: nothing waits
    /// behind more than the window's other messages.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<SparkMessage>> PriorityWindow(
        IEnumerable<SparkMessage> window, Func<string, SparkQueuePriority> priorityOf)
        => window
            .Where(m => m.Id is not null)
            .GroupBy(m => priorityOf(m.QueueName))
            .OrderByDescending(g => g.Key)
            .Select(g => (IReadOnlyList<SparkMessage>)g.ToList())
            .ToList();

    protected override SubscriptionCreationOptions ConfigureSubscription()
    {
        // No QueueName predicate — that is the whole point. It also retires the RQL-injection
        // surface that QueueNames.IsValid existed to guard: with no queue name interpolated into
        // the query, a hostile queue name has nothing to break out of.
        //
        // The status arms are unchanged. Note WakeUp where a time comparison would be natural:
        // `NextAttemptAtUtc <= now()` in a subscription where-clause silently never matches, so
        // "the backoff has elapsed" is materialized as plain field state by MessageRetrySweeper,
        // which can evaluate time. Never put now() here (invariant 7).
        return new SubscriptionCreationOptions
        {
            Query = $@"from SparkMessages where (Status = '{nameof(EMessageStatus.Pending)}' and (NextAttemptAtUtc = null or WakeUp = true)) or (Status = '{nameof(EMessageStatus.Failed)}' and WakeUp = true)"
        };
    }

    protected override async Task ProcessBatchAsync(SubscriptionBatch<SparkMessage> batch, CancellationToken cancellationToken)
    {
        // GroupBy keeps the source order inside each group, so a queue's messages stay in etag order.
        foreach (var group in PriorityWindow(batch.Items.Select(i => i.Result), options.PriorityFor))
            await FeedGroupAsync(batch, group, cancellationToken);
    }

    /// <summary>
    /// One priority group: one load, the admission decision per message, one write for every claim and
    /// deferral, then routing. A conflict on that write (another feeder, or the sweeper, touched one of
    /// the messages) fails it as a whole, and the group is then fed one message at a time.
    /// </summary>
    private async Task FeedGroupAsync(SubscriptionBatch<SparkMessage> batch, IReadOnlyList<SparkMessage> group, CancellationToken cancellationToken)
    {
        var ids = group.Select(m => m.Id!).ToArray();
        var decisions = new Dictionary<string, AdmissionDecision>(StringComparer.Ordinal);
        var toRoute = new List<SparkMessage>();

        using (var session = batch.OpenAsyncSession())
        {
            // Without this the claim is last-write-wins, and two feeders would both believe they
            // claimed the same message and both run its handlers.
            session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
            var loaded = await session.LoadAsync<SparkMessage>(ids, cancellationToken);

            var now = DateTime.UtcNow;
            foreach (var id in ids)
            {
                if (loaded.GetValueOrDefault(id) is not { } tracked || tracked.Status == EMessageStatus.Processing)
                {
                    Logger.LogDebug("Message {MessageId} is gone or already claimed; skipping", id);
                    continue;
                }

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
            catch (Raven.Client.Exceptions.ConcurrencyException)
            {
                Logger.LogDebug("A message in a window of {Count} changed while being claimed; claiming them one at a time", decisions.Count);
                toRoute = null;
            }
        }

        if (toRoute is null)
        {
            toRoute = [];
            foreach (var (id, decision) in decisions)
            {
                if (await FeedOneAsync(batch, id, decision, cancellationToken) is { } claimed)
                    toRoute.Add(claimed);
            }
        }

        // Claimed and durably saved BEFORE the batch is acknowledged. If this process dies from here
        // on, the message sits at Processing with an expiring claim and the sweeper returns it to the
        // queue. That ordering is the webhook-drop fix: previously the batch could be acknowledged for
        // a message whose Processing status nothing ever read again.
        foreach (var message in toRoute)
            await router.RouteAsync(message.QueueName, message.Id!, cancellationToken);
    }

    /// <summary>The fallback for a conflicting group write: the decision already taken, one message at a time.</summary>
    private async Task<SparkMessage?> FeedOneAsync(SubscriptionBatch<SparkMessage> batch, string id, AdmissionDecision decision, CancellationToken cancellationToken)
    {
        using var session = batch.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;

        var tracked = await session.LoadAsync<SparkMessage>(id, cancellationToken);

        // Already claimed by someone else, or no longer eligible. The subscription can deliver a
        // document more than once (a reconnect replays an unacknowledged batch), so this is an
        // ordinary occurrence, not an error.
        if (tracked is null || tracked.Status == EMessageStatus.Processing)
        {
            if (decision.Kind != AdmissionKind.Defer)
                admission.Forget(id);
            return null;
        }

        if (decision.Kind == AdmissionKind.Defer)
        {
            Defer(tracked, decision.Slot);
            try { await session.SaveChangesAsync(cancellationToken); }
            catch (Raven.Client.Exceptions.ConcurrencyException) { Logger.LogDebug("Message {MessageId} changed while being deferred; leaving it", id); }
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
