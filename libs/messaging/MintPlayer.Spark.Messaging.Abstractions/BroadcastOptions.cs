namespace MintPlayer.Spark.Messaging.Abstractions;

/// <summary>
/// Per-publish settings for <see cref="IMessageBus.BroadcastAsync{TMessage}(TMessage, BroadcastOptions, CancellationToken)"/>.
/// Every property is optional; an empty instance behaves exactly like a plain
/// <see cref="IMessageBus.BroadcastAsync{TMessage}(TMessage, CancellationToken)"/>.
/// </summary>
public sealed record BroadcastOptions
{
    /// <summary>
    /// Enqueue at most once for this key: a second publish of the same message type with the same
    /// key does nothing while the first document exists (see
    /// <see cref="IMessageBus.BroadcastOnceAsync{TMessage}(TMessage, string, CancellationToken)"/>).
    /// <para>
    /// The key is <b>namespaced by message type</b> and <b>hashed</b> into the document id, so two
    /// message types may use the same key, and two keys that differ only in characters an id cannot
    /// hold (<c>a:b</c> and <c>a/b</c>) or in letter case no longer collide.
    /// </para>
    /// </summary>
    public string? DeduplicationKey { get; init; }

    /// <summary>Do not deliver before this much time has passed.</summary>
    public TimeSpan? Delay { get; init; }

    /// <summary>
    /// Attempts per handler before it is dead-lettered. Overrides the queue's
    /// <c>SparkQueueOptions.MaxAttempts</c> and the global <c>SparkMessagingOptions.MaxAttempts</c>.
    /// </summary>
    public int? MaxAttempts { get; init; }

    /// <summary>
    /// After this instant the message is never handled: it is dead-lettered with reason
    /// <c>Expired</c> instead — when it is picked up after the instant, when a retry or a throttle
    /// slot would fall after it, whichever comes first. The motivating case is a password-reset mail
    /// whose token expires: sending it late is worse than not sending it.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; init; }

    /// <summary>
    /// Publish onto this queue instead of the one the message type declares
    /// (<see cref="MessageQueueAttribute"/>, or the derived type name). The queue must be declared
    /// under <c>SparkMessagingOptions.Queues</c> (<c>Spark:Messaging:Queues:{name}</c>) — an
    /// undeclared name is refused at publish, because a queue that no consumer knows about is a
    /// queue whose messages are never handled.
    /// </summary>
    public string? Queue { get; init; }

    /// <summary>
    /// Replace the stored payload with an empty string once the message reaches a terminal status
    /// (Completed or DeadLettered). For payloads carrying secrets — reset or confirmation tokens —
    /// that would otherwise sit in the database for the whole retention window. A scrubbed
    /// dead-letter cannot be replayed; that is the price.
    /// </summary>
    public bool ScrubPayloadOnTerminal { get; init; }
}
