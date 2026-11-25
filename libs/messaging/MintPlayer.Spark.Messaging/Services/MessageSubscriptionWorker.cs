using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.SubscriptionWorker;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// One subscription for a single queue — the pre-rework delivery model, kept for
/// <see cref="ESubscriptionMode.SubscriptionPerQueue"/>.
/// <para>
/// It costs one subscription per queue name, which is why it is no longer the default: RavenDB caps
/// subscriptions per database (3 on Community), so this model made "how many queues may this
/// application have?" a licensing question, and exceeding the cap failed silently. It remains
/// available because it buys something real where the licence allows it — isolation enforced by the
/// server rather than by in-process lanes, so a queue's documents are not even delivered to a host
/// that is busy with another queue.
/// </para>
/// <para>
/// The per-message contract lives in <see cref="MessageProcessor"/>, shared verbatim with the
/// single-subscription path. This class only delivers and claims; the two modes must not be able to
/// drift in how a message is handled.
/// </para>
/// </summary>
internal sealed class MessageSubscriptionWorker : SparkSubscriptionWorker<SparkMessage>
{
    private readonly string _queueName;
    private readonly IServiceProvider _serviceProvider;
    private readonly SparkMessagingOptions _options;
    private readonly ISparkStore _store;

    protected override string SubscriptionName => $"SparkMessaging-{_queueName}";
    protected override int MaxDocsPerBatch => 1;

    // Hand-written ctor — the queueName is a per-instance runtime value supplied by
    // MessageSubscriptionManager and can't be DI-resolved, so [Inject] doesn't apply here.
    // Forwards (subscriptions, loggerFactory) to the base (which uses [PostConstruct] to derive the
    // per-class logger via loggerFactory.CreateLogger(GetType())).
    public MessageSubscriptionWorker(
        string queueName,
        ISparkSubscriptions subscriptions,
        ISparkStore store,
        IServiceProvider serviceProvider,
        IOptions<SparkMessagingOptions> options,
        ILoggerFactory loggerFactory)
        : base(subscriptions, loggerFactory)
    {
        _queueName = queueName;
        _store = store;
        _serviceProvider = serviceProvider;
        _options = options.Value;
    }

    protected override Expression<Func<SparkMessage, bool>> Filter
    {
        get
        {
            // R2-H14: refuse queue names that don't match the strict identifier allowlist. The filter is typed now
            // (#536 D39), but RavenDB still renders the name into the subscription's RQL. (The single-subscription feeder has no QueueName
            // predicate at all, so it has no such surface — this validation is why the two differ.)
            // An earlier EscapeRql helper escaped the single quote but not the backslash, so `\\'`
            // round-tripped to `\\\'` and still closed the literal. The set of valid queue names is
            // bounded — developer-declared via attribute, or an explicit override — so a strict
            // allowlist is the right shape. Throw at startup so the operator sees it before traffic.
            if (!QueueNames.IsValid(_queueName))
                throw new InvalidOperationException(
                    $"Invalid Spark message queue name '{_queueName}'. Queue names must match [A-Za-z0-9._+`-]+.");

            // Both halves of the designed pickup condition: new messages, AND messages whose retry
            // backoff or broadcast delay has elapsed. Subscriptions re-evaluate a document only when it
            // is written, so nothing could ever match a parked message again on its own; and a
            // where-clause cannot evaluate time — `NextAttemptAtUtc <= now()` silently never matches,
            // verified empirically. So "the backoff has elapsed" is materialized as plain field state
            // by MessageRetrySweeper, whose patch also bumps the change vector that triggers
            // re-evaluation. Never put now() here (invariant 7).
            var queueName = _queueName;
            return m => m.QueueName == queueName
                && ((m.Status == EMessageStatus.Pending && (m.NextAttemptAtUtc == null || m.WakeUp == true))
                    || (m.Status == EMessageStatus.Failed && m.WakeUp == true));
        }
    }

    protected override async Task ProcessBatchAsync(SparkSubscriptionBatch<SparkMessage> batch, CancellationToken cancellationToken)
    {
        var processor = _serviceProvider.GetRequiredService<MessageProcessor>();

        foreach (var item in batch.Items)
        {
            using var session = _store.OpenSession();
            session.OptimisticConcurrency = true;

            var message = await session.LoadAsync<SparkMessage>(item.Id, cancellationToken);
            if (message is null)
                continue;

            if (message.Status == EMessageStatus.Processing)
            {
                Logger.LogDebug("Message {MessageId} is already claimed by {OwnerId}; skipping",
                    message.Id, message.OwnerId ?? "<none>");
                continue;
            }

            // Claim before handling, and save before any handler runs, for the same reason as the
            // feeder: a process that dies mid-handler must leave a record that says so, or the
            // message strands at Processing with nothing able to see it.
            if (!await MessageClaims.TryClaimAsync(session, message, MessageClaims.NodeId, _options.ClaimTtl, cancellationToken))
            {
                Logger.LogDebug("Lost the race to claim message {MessageId}", message.Id);
                continue;
            }

            // The same guards as a single-subscription pump: renew the claim while the handlers run,
            // and cancel a handler that exceeds HandlerTimeout. This path used to run the handlers
            // with neither, so a handler slower than ClaimTtl was reclaimed by the sweeper and handled
            // twice, and a hung handler held this queue's subscription for ever.
            var messageId = message.Id!;
            await ClaimedExecution.RunAsync(
                _store, _options, Logger, messageId, _options.HandlerTimeoutFor(_queueName),
                token => processor.ProcessAsync(messageId, MessageClaims.NodeId, token, cancellationToken),
                cancellationToken);
        }
    }
}
