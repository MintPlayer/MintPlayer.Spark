using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.SubscriptionWorker;
using Raven.Client.Documents;
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
    private readonly SparkMessagingOptions options;

    protected override string SubscriptionName => SubscriptionNameConstant;

    /// <summary>
    /// One document per batch. The feeder's work per message is a single claim write, so batching
    /// buys little, and a batch is acknowledged as a unit — a larger batch would hold a claim on
    /// every message in it while the first one waits for lane capacity.
    /// </summary>
    protected override int MaxDocsPerBatch => 1;

    // Hand-written ctor: this worker is constructed by MessageSubscriptionManager rather than
    // resolved from DI, because it only exists while this host holds the messaging lease.
    public MessageFeeder(
        MessageQueueRouter router,
        IDocumentStore store,
        IOptions<SparkMessagingOptions> options,
        ILoggerFactory loggerFactory)
        : base(loggerFactory, store)
    {
        this.router = router;
        this.options = options.Value;
    }

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
        foreach (var item in batch.Items)
        {
            var message = item.Result;
            if (message.Id is null)
                continue;

            using var session = batch.OpenAsyncSession();
            // Without this the claim is last-write-wins, and two feeders would both believe they
            // claimed the same message and both run its handlers.
            session.Advanced.UseOptimisticConcurrency = true;

            // Re-load inside our own session: item.Result came from the batch's deserialization and
            // is not tracked, so saving it would not carry a change vector to check.
            var tracked = await session.LoadAsync<SparkMessage>(message.Id, cancellationToken);
            if (tracked is null)
                continue;

            // Already claimed by someone else, or no longer eligible. The subscription can deliver
            // a document more than once (a reconnect replays an unacknowledged batch), so this is
            // an ordinary occurrence, not an error.
            if (tracked.Status == EMessageStatus.Processing)
            {
                Logger.LogDebug("Message {MessageId} is already claimed by {OwnerId}; skipping",
                    tracked.Id, tracked.OwnerId ?? "<none>");
                continue;
            }

            var claimed = await MessageClaims.TryClaimAsync(
                session, tracked, MessageClaims.NodeId, options.ClaimTtl, cancellationToken);

            if (!claimed)
            {
                Logger.LogDebug("Lost the race to claim message {MessageId}; another host has it", tracked.Id);
                continue;
            }

            // Claimed and durably saved BEFORE the batch is acknowledged. If this process dies from
            // here on, the message sits at Processing with an expiring claim and the sweeper
            // returns it to the queue. That ordering is the webhook-drop fix: previously the batch
            // could be acknowledged for a message whose Processing status nothing ever read again.
            await router.RouteAsync(tracked.QueueName, tracked.Id!, cancellationToken);
        }
    }
}
