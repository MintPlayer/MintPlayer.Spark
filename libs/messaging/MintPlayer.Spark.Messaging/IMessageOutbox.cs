using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Messaging;

/// <summary>
/// Publishing inside someone else's transaction (#467, S13): the message is stored in the caller's
/// session and commits with the caller's <c>SaveChangesAsync</c> — or not at all. "The data changed"
/// and "the follow-up is queued" can then never disagree, even across a crash.
/// </summary>
public interface IMessageOutbox
{
    /// <summary>
    /// Stores <paramref name="message"/> in <paramref name="session"/> under a fresh id. Only stores:
    /// the caller saves. Every <see cref="BroadcastOptions"/> setting applies except
    /// <see cref="BroadcastOptions.DeduplicationKey"/>, which is refused: deduplication needs optimistic
    /// concurrency on the whole session, and a duplicate would then roll back the caller's own write.
    /// </summary>
    Task EnqueueAsync<TMessage>(ISparkSession session, TMessage message, BroadcastOptions? options = null, CancellationToken cancellationToken = default);
}
