using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Messaging;

/// <summary>
/// Publishing inside someone else's transaction (#467, S13): the message is stored in the scope's session and
/// commits with that session's <c>SaveChangesAsync</c> — or not at all. "The data changed" and "the follow-up is
/// queued" can then never disagree, even across a crash.
/// </summary>
/// <remarks>
/// The outbox is scoped and writes to the scope's <c>ISparkSession</c> (#536 D50). On RavenDB that wraps the scope's
/// <c>IAsyncDocumentSession</c>, so code that writes through either one shares the unit of work. To write in another
/// unit of work, resolve the outbox from another scope.
/// </remarks>
public interface IMessageOutbox
{
    /// <summary>
    /// Stores <paramref name="message"/> in the scope's session under a fresh id. Only stores: the caller saves.
    /// Every <see cref="BroadcastOptions"/> setting applies except <see cref="BroadcastOptions.DeduplicationKey"/>,
    /// which is refused: deduplication needs optimistic concurrency on the whole session, and a duplicate would then
    /// roll back the caller's own write.
    /// </summary>
    Task EnqueueAsync<TMessage>(TMessage message, BroadcastOptions? options = null, CancellationToken cancellationToken = default);
}
