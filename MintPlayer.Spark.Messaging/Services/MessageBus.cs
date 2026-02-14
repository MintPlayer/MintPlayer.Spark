using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Messaging.Services;

internal class MessageBus : IMessageBus
{
    private readonly IDocumentStore _documentStore;
    private readonly SparkMessagingOptions _options;

    public MessageBus(IDocumentStore documentStore, IOptions<SparkMessagingOptions> options)
    {
        _documentStore = documentStore;
        _options = options.Value;
    }

    public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        => StoreMessageAsync(message, delay: null, queueNameOverride: null, cancellationToken);

    public Task BroadcastAsync<TMessage>(TMessage message, string queueName, CancellationToken cancellationToken = default)
        => StoreMessageAsync(message, delay: null, queueNameOverride: queueName, cancellationToken);

    public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
        => StoreMessageAsync(message, delay, queueNameOverride: null, cancellationToken);

    private async Task StoreMessageAsync<TMessage>(TMessage message, TimeSpan? delay, string? queueNameOverride, CancellationToken cancellationToken)
    {
        var messageType = typeof(TMessage);

        var queueName = queueNameOverride;
        if (queueName == null)
        {
            var queueAttribute = messageType.GetCustomAttribute<MessageQueueAttribute>();
            queueName = queueAttribute?.QueueName ?? messageType.FullName!;
        }

        var payloadJson = JsonSerializer.Serialize(message);

        var sparkMessage = new SparkMessage
        {
            QueueName = queueName,
            MessageType = messageType.AssemblyQualifiedName!,
            PayloadJson = payloadJson,
            CreatedAtUtc = DateTime.UtcNow,
            NextAttemptAtUtc = delay.HasValue ? DateTime.UtcNow + delay.Value : null,
            AttemptCount = 0,
            MaxAttempts = _options.MaxAttempts,
            Status = EMessageStatus.Pending,
        };

        using var session = _documentStore.OpenAsyncSession();
        await session.StoreAsync(sparkMessage, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }
}
