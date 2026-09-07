namespace MintPlayer.Spark.Messaging.Abstractions;

public interface IMessageBus
{
    Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default);
    Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcasts a message at most once for a given <paramref name="deduplicationKey"/>: if a
    /// message with that key has already been enqueued, this does nothing.
    /// <para>
    /// The key becomes part of the message's document id, so uniqueness is enforced by the database
    /// rather than by a query, and it holds regardless of which host receives the duplicate.
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
    Task BroadcastOnceAsync<TMessage>(TMessage message, string deduplicationKey, CancellationToken cancellationToken = default);
}
