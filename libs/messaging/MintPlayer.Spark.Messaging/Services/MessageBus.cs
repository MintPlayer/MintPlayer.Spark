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

    private SparkMessagingOptions Options => options.Value;

    public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        => StoreMessageAsync(message, delay: null, deduplicationKey: null, cancellationToken);

    public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
        => StoreMessageAsync(message, delay, deduplicationKey: null, cancellationToken);

    public Task BroadcastOnceAsync<TMessage>(TMessage message, string deduplicationKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deduplicationKey))
            throw new ArgumentException("A deduplication key is required.", nameof(deduplicationKey));

        return StoreMessageAsync(message, delay: null, deduplicationKey, cancellationToken);
    }

    private async Task StoreMessageAsync<TMessage>(
        TMessage message, TimeSpan? delay, string? deduplicationKey, CancellationToken cancellationToken)
    {
        var messageType = typeof(TMessage);
        var queueName = QueueNames.ForMessageType(messageType);
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

        if (deduplicationKey is null)
        {
            await session.StoreAsync(sparkMessage, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
            return;
        }

        // The key goes in the document id, so the database enforces uniqueness. Sanitized because
        // a RavenDB id may not contain arbitrary characters, and the key comes from a request
        // header.
        var id = $"SparkMessages/{Sanitize(deduplicationKey)}";

        if (await session.Advanced.ExistsAsync(id, cancellationToken))
            return;

        // Empty change vector plus optimistic concurrency means "create only": if another host
        // stored the same key between the check above and this save, the save fails rather than
        // overwriting — which matters because the existing document may already be mid-flight, and
        // overwriting it would reset a message someone is processing back to Pending.
        session.Advanced.UseOptimisticConcurrency = true;
        await session.StoreAsync(sparkMessage, changeVector: string.Empty, id, cancellationToken);

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            // Already enqueued by a concurrent caller. That is the requested behaviour.
        }
    }

    private static string Sanitize(string key)
        => string.Create(key.Length, key, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_';
            }
        });
}
