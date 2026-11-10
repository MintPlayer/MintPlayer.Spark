namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>One committed change, as a dirty bit: never its content.</summary>
/// <param name="DocumentId">The document, when known.</param>
/// <param name="Collection">Its collection, when known.</param>
/// <param name="Version">The version the write produced, when known. Live updates de-duplicate a write that both halves
/// of the feed report by id and version.</param>
public readonly record struct SparkStoreChange(string? DocumentId, string? Collection, string? Version);

/// <summary>
/// The store's change notifications, which live updates are built on (#551 D19, docs/spark_editor_PRD.md §8.2 S10). It is
/// the <see cref="SparkStoreCapabilities.ChangeNotifications"/> capability: a provider without it registers none.
/// </summary>
/// <remarks>
/// Two halves, because they differ in what they can see and in how reliable they are:
/// <list type="bullet">
/// <item><see cref="OnCommitted"/>: every write this process commits through a session of the store, after the commit.
/// Nothing app code has to call. RavenDB raises it from the document store's own events, an in-memory store from its
/// commit.</item>
/// <item><see cref="WatchDocument"/> / <see cref="WatchCollection"/>: the store's own notifications, which also carry
/// other processes' writes. Not durable: <see cref="Reconnected"/> says notifications may have been lost, and the
/// consumer re-evaluates everything.</item>
/// </list>
/// ⚠️ A handler must never throw into a writer; the provider swallows what a handler throws.
/// </remarks>
public interface ISparkChangeFeed
{
    /// <summary>Starts reporting this process's committed writes to <paramref name="handler"/>; dispose to stop.</summary>
    IDisposable OnCommitted(Action<SparkStoreChange> handler);

    /// <summary>
    /// Watches one document through the store's notifications. <see cref="SparkChangeWatch.Confirmed"/> completes when the
    /// store has confirmed the watch, so a read that follows cannot miss a write landing in between. Throws when the
    /// store cannot watch; the caller then relies on <see cref="OnCommitted"/>.
    /// </summary>
    SparkChangeWatch WatchDocument(string id, Action<SparkStoreChange> handler);

    /// <summary>Watches every document of one collection; see <see cref="WatchDocument"/>.</summary>
    SparkChangeWatch WatchCollection(string collection, Action<SparkStoreChange> handler);

    /// <summary>Raised when the notification connection is back after a loss: changes may have been missed.</summary>
    event Action? Reconnected;
}

/// <summary>A watch on the store's notifications: dispose <see cref="Subscription"/> to stop it.</summary>
public sealed record SparkChangeWatch(IDisposable Subscription, Task Confirmed);
