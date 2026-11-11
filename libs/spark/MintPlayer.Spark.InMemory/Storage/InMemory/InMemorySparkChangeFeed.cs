using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Storage.InMemory;

/// <summary>
/// <see cref="ISparkChangeFeed"/> over the database's commits (#551 S10). One process holds every writer, so the
/// in-process feed and the watches see the same events, a watch is live the moment it is made, and there is no
/// connection to lose: <see cref="Reconnected"/> never fires.
/// </summary>
[Register(typeof(ISparkChangeFeed), ServiceLifetime.Singleton)]
internal sealed partial class InMemorySparkChangeFeed : ISparkChangeFeed
{
    [Inject] private readonly InMemoryDatabase database;

    public IDisposable OnCommitted(Action<SparkStoreChange> handler)
        => Subscribe(changes =>
        {
            foreach (var change in changes)
                handler(change);
        });

    public SparkChangeWatch WatchDocument(string id, Action<SparkStoreChange> handler)
        => new(Subscribe(changes =>
        {
            foreach (var change in changes)
                if (string.Equals(change.DocumentId, id, StringComparison.OrdinalIgnoreCase))
                    handler(change);
        }), Task.CompletedTask);

    public SparkChangeWatch WatchCollection(string collection, Action<SparkStoreChange> handler)
        => new(Subscribe(changes =>
        {
            foreach (var change in changes)
                if (string.Equals(change.Collection, collection, StringComparison.OrdinalIgnoreCase))
                    handler(change);
        }), Task.CompletedTask);

    public event Action? Reconnected { add { } remove { } }

    private Subscription Subscribe(Action<IReadOnlyList<SparkStoreChange>> handler)
    {
        database.Committed += handler;
        return new Subscription(() => database.Committed -= handler);
    }

    private sealed class Subscription(Action remove) : IDisposable
    {
        private Action? remove = remove;

        public void Dispose() => Interlocked.Exchange(ref remove, null)?.Invoke();
    }
}
