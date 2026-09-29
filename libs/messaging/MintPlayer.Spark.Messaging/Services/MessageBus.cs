using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using Raven.Client.Documents;
using Newtonsoft.Json;

namespace MintPlayer.Spark.Messaging.Services;

internal partial class MessageBus : IMessageBus
{
    /// <summary>
    /// Longest readable prefix kept from a deduplication key. The hash guarantees uniqueness, so the
    /// prefix only has to be long enough for a person reading the database to recognise the key.
    /// </summary>
    internal const int MaxReadableKeyLength = 64;

    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkMessagingOptions> options;

    private SparkMessagingOptions Options => options.Value;

    public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        => BroadcastAsync(message, new BroadcastOptions(), cancellationToken);

    public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
        => BroadcastAsync(message, new BroadcastOptions { Delay = delay }, cancellationToken);

    public Task BroadcastOnceAsync<TMessage>(TMessage message, string deduplicationKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deduplicationKey))
            throw new ArgumentException("A deduplication key is required.", nameof(deduplicationKey));

        return BroadcastAsync(message, new BroadcastOptions { DeduplicationKey = deduplicationKey }, cancellationToken);
    }

    public async Task BroadcastAsync<TMessage>(TMessage message, BroadcastOptions broadcastOptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broadcastOptions);
        if (broadcastOptions.DeduplicationKey is { } key && string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A deduplication key must not be blank.", nameof(broadcastOptions));

        var messageType = typeof(TMessage);
        var queueName = ResolveQueue(messageType, broadcastOptions.Queue);
        var queueOptions = Options.QueueOptionsFor(queueName);
        var now = DateTime.UtcNow;

        var sparkMessage = new SparkMessage
        {
            QueueName = queueName,
            MessageType = messageType.AssemblyQualifiedName!,
            PayloadJson = JsonConvert.SerializeObject(message),
            CreatedAtUtc = now,
            NextAttemptAtUtc = broadcastOptions.Delay is { } delay ? now + delay : null,
            Priority = (int)Options.PriorityFor(queueName),
            AttemptCount = 0,
            MaxAttempts = broadcastOptions.MaxAttempts ?? queueOptions?.MaxAttempts ?? Options.MaxAttempts,
            Status = EMessageStatus.Pending,
            ExpiresAtUtc = broadcastOptions.ExpiresAtUtc,
            ScrubPayloadOnTerminal = broadcastOptions.ScrubPayloadOnTerminal,
        };

        using var session = documentStore.OpenAsyncSession();

        if (broadcastOptions.DeduplicationKey is not { } deduplicationKey)
        {
            await session.StoreAsync(sparkMessage, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
            return;
        }

        // The key goes in the document id, so the database enforces uniqueness.
        var id = DeduplicationId(messageType, deduplicationKey);

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

    /// <summary>
    /// The document id for a deduplicated message: <c>SparkMessages/{readable}.{hash}</c>.
    /// <para>
    /// It used to be <c>SparkMessages/{sanitized key}</c>, which collided in three ways: every
    /// character an id cannot hold became <c>_</c> (so <c>a:b</c>, <c>a/b</c> and <c>a_b</c> were one
    /// key); RavenDB ids are case-insensitive (so <c>Abc</c> and <c>abc</c> were one key); and the id
    /// carried no message type (so two message types using the same delivery id suppressed each
    /// other — which is why CodeCoverage had to prefix its automation key by hand). The hash covers
    /// the versionless message type and the exact key, so all three are distinct now; the readable
    /// prefix is kept so the database stays legible.
    /// </para>
    /// </summary>
    internal static string DeduplicationId(Type messageType, string deduplicationKey)
    {
        // Versionless on purpose: the assembly-qualified name changes with every package version, and
        // a deploy between a delivery and its redelivery must not defeat the deduplication.
        var typeName = QueueNames.Derive(messageType);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{typeName}\n{deduplicationKey}"));
        var readable = Sanitize(deduplicationKey.Length > MaxReadableKeyLength
            ? deduplicationKey[..MaxReadableKeyLength]
            : deduplicationKey);
        return $"SparkMessages/{readable}.{Convert.ToHexStringLower(hash, 0, 16)}";
    }

    /// <summary>
    /// The queue a publish lands on. An override must be a declared queue: a message on a queue no
    /// consumer knows about is never handled (the reason the old <c>BroadcastAsync(message, queue)</c>
    /// overload was deleted), and declaring it is what gives it a worker in per-queue mode.
    /// </summary>
    private string ResolveQueue(Type messageType, string? queueOverride)
    {
        var declared = QueueNames.ForMessageType(messageType);
        if (queueOverride is null || queueOverride == declared)
            return declared;

        if (!QueueNames.IsValid(queueOverride))
            throw new ArgumentException(
                $"Invalid Spark message queue name '{queueOverride}'. Queue names must match [A-Za-z0-9._+`-]+.",
                nameof(queueOverride));

        if (!Options.Queues.ContainsKey(queueOverride))
            throw new InvalidOperationException(
                $"BroadcastOptions.Queue '{queueOverride}' is not a declared queue. Declare it under "
                + $"Spark:Messaging:Queues:{queueOverride} (or SparkMessagingOptions.Queues in code) so a "
                + "consumer exists for it.");

        return queueOverride;
    }

    private static string Sanitize(string key)
        => string.Create(key.Length, key, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_';
            }
        });
}
