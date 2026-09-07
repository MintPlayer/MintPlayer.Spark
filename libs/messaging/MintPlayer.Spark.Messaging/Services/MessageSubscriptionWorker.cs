using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.SubscriptionWorker;
using Raven.Client.Documents;
using Raven.Client.Documents.Subscriptions;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// One RavenDB data subscription for a single queue — the pre-rework delivery model, kept for
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

    protected override string SubscriptionName => $"SparkMessaging-{_queueName}";
    protected override int MaxDocsPerBatch => 1;

    // Hand-written ctor — the queueName is a per-instance runtime value supplied by
    // MessageSubscriptionManager and can't be DI-resolved, so [Inject] doesn't apply here.
    // Forwards (store, loggerFactory) to the base (which uses [PostConstruct] to derive the
    // per-class logger via loggerFactory.CreateLogger(GetType())).
    public MessageSubscriptionWorker(
        string queueName,
        IDocumentStore store,
        IServiceProvider serviceProvider,
        IOptions<SparkMessagingOptions> options,
        ILoggerFactory loggerFactory)
        : base(loggerFactory, store)
    {
        _queueName = queueName;
        _serviceProvider = serviceProvider;
        _options = options.Value;
    }

    protected override SubscriptionCreationOptions ConfigureSubscription()
    {
        // R2-H14: refuse queue names that don't match the strict identifier allowlist, because this
        // mode interpolates the name into RQL. (The single-subscription feeder has no QueueName
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
        return new SubscriptionCreationOptions
        {
            Query = $@"from SparkMessages where QueueName = '{_queueName}' and ((Status = '{nameof(EMessageStatus.Pending)}' and (NextAttemptAtUtc = null or WakeUp = true)) or (Status = '{nameof(EMessageStatus.Failed)}' and WakeUp = true))"
        };
    }

    protected override async Task ProcessBatchAsync(SubscriptionBatch<SparkMessage> batch, CancellationToken cancellationToken)
    {
        var processor = _serviceProvider.GetRequiredService<MessageProcessor>();

        foreach (var item in batch.Items)
        {
            if (item.Result.Id is null)
                continue;

            using var session = batch.OpenAsyncSession();
            session.Advanced.UseOptimisticConcurrency = true;

            var message = await session.LoadAsync<SparkMessage>(item.Result.Id, cancellationToken);
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

            await processor.RunHandlersAsync(session, message, cancellationToken);
        }
    }
}
