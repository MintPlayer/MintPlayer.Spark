namespace MintPlayer.Spark.Messaging.Abstractions;

public interface IMessageBus
{
    /// <summary>
    /// Publishes a message with per-publish settings: deduplication, delay, attempts, expiry, a
    /// queue override and payload scrubbing. Every other publish method is this one with a fixed
    /// <see cref="BroadcastOptions"/>.
    /// </summary>
    Task BroadcastAsync<TMessage>(TMessage message, BroadcastOptions options, CancellationToken cancellationToken = default);

    Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        => BroadcastAsync(message, new BroadcastOptions(), cancellationToken);

    Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
        => BroadcastAsync(message, new BroadcastOptions { Delay = delay }, cancellationToken);

    /// <summary>
    /// Broadcasts a message at most once for a given <paramref name="deduplicationKey"/>: if a
    /// message of the same type with that key has already been enqueued, this does nothing.
    /// <para>
    /// The key becomes part of the message's document id, so uniqueness is enforced by the database
    /// rather than by a query, and it holds regardless of which host receives the duplicate. The id
    /// is <c>SparkMessages/{readable key}.{hash}</c>, where the hash covers the message type and the
    /// exact key: the readable part is only for people reading the database.
    /// </para>
    /// <para>
    /// The motivating case is a GitHub webhook redelivery. GitHub sends a stable
    /// <c>X-GitHub-Delivery</c> id and will re-send the same delivery — automatically after a 5xx,
    /// or manually from the UI — so without a key the same event is enqueued twice and every
    /// recipient runs twice. Note that this deduplicates <i>enqueueing</i>, not handling: a
    /// duplicate arriving after the original has been processed and expired by retention is a new
    /// message again, which is the correct trade-off for a retention window measured in days.
    /// </para>
    /// </summary>
    Task BroadcastOnceAsync<TMessage>(TMessage message, string deduplicationKey, CancellationToken cancellationToken = default)
        => BroadcastAsync(message, new BroadcastOptions { DeduplicationKey = deduplicationKey }, cancellationToken);
}
