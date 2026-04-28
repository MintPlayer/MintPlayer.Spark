using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using Raven.Client.Documents;
using System.Reflection;
using Newtonsoft.Json;

namespace MintPlayer.Spark.Messaging.Services;

internal partial class MessageBus : IMessageBus
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkMessagingOptions> options;

    private SparkMessagingOptions Options => options.Value;

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

        var payloadJson = JsonConvert.SerializeObject(message);

        var sparkMessage = new SparkMessage
        {
            QueueName = queueName,
            MessageType = messageType.AssemblyQualifiedName!,
            PayloadJson = payloadJson,
            CreatedAtUtc = DateTime.UtcNow,
            NextAttemptAtUtc = delay.HasValue ? DateTime.UtcNow + delay.Value : null,
            AttemptCount = 0,
            MaxAttempts = Options.MaxAttempts,
            Status = EMessageStatus.Pending,
        };

        using var session = documentStore.OpenAsyncSession();
        await session.StoreAsync(sparkMessage, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }
}
