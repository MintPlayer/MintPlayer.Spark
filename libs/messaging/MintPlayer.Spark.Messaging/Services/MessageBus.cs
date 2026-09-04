using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using Raven.Client.Documents;
using Newtonsoft.Json;

namespace MintPlayer.Spark.Messaging.Services;

internal partial class MessageBus : IMessageBus
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkMessagingOptions> options;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly MessageSequence sequence;
    [Inject] private readonly ILaneRegistry lanes;

    private SparkMessagingOptions Options => options.Value;

    public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        => StoreMessageAsync(message, delay: null, queueNameOverride: null, cancellationToken);

    public Task BroadcastAsync<TMessage>(TMessage message, string queueName, CancellationToken cancellationToken = default)
        => StoreMessageAsync(message, delay: null, queueNameOverride: queueName, cancellationToken);

    public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
        => StoreMessageAsync(message, delay, queueNameOverride: null, cancellationToken);

    public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, string queueName, CancellationToken cancellationToken = default)
        => StoreMessageAsync(message, delay, queueNameOverride: queueName, cancellationToken);

    private async Task StoreMessageAsync<TMessage>(TMessage message, TimeSpan? delay, string? queueNameOverride, CancellationToken cancellationToken)
    {
        var messageType = typeof(TMessage);

        var queueName = queueNameOverride ?? QueueNames.ForMessageType(messageType);

        var payloadJson = JsonConvert.SerializeObject(message);

        var now = timeProvider.GetUtcNow().UtcDateTime;

        var sparkMessage = new SparkMessage
        {
            QueueName = queueName,
            MessageType = messageType.AssemblyQualifiedName!,
            PayloadJson = payloadJson,
            CreatedAtUtc = now,
            // The ordering key. Issued monotonically so that a producer broadcasting in a loop —
            // an ingestion burst does exactly this — cannot tie, and so an NTP step backwards
            // cannot invert two messages of one partition.
            Sequence = sequence.Next(),
            // Resolved once, here, and persisted. The lane's pump never recomputes it: a selector
            // that changed would move existing messages between partitions.
            PartitionKey = lanes.PartitionKeyFor(queueName, messageType, message!) ?? string.Empty,
            // A delay is a scheduling instruction, not a dependency, so it must not block the
            // partition the way a retry backoff does — hence a separate field.
            VisibleAtUtc = delay.HasValue ? now + delay.Value : null,
            AttemptCount = 0,
            Status = EMessageStatus.Pending,
        };

        using var session = documentStore.OpenAsyncSession();
        await session.StoreAsync(sparkMessage, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }
}
