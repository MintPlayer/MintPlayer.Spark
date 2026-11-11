using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Changes;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Storage.Raven;

/// <summary>
/// RavenDB's <see cref="ISparkChangeFeed"/> (#551 D19): the document store's own session events for this process's
/// commits, and the Changes API for the watches. Moved out of core's <c>LiveActionsSource</c> and <c>LiveHub</c>.
/// </summary>
/// <remarks>
/// <para>
/// A stored document is reported from <c>OnAfterSaveChanges</c>, after the commit, with its new change vector. A delete
/// raises no after-commit event in the RavenDB client, so it is remembered from <c>OnBeforeDelete</c> and reported when
/// that session next commits a store, or when it is disposed. A delete whose save then failed is a spurious dirty bit,
/// which costs one re-evaluation and no frame. Patches, bulk inserts and deletes by id that were never loaded raise no
/// event at all: code using them calls <c>ISparkLiveNotifier</c>.
/// </para>
/// <para>
/// ⚠️ A handler never throws into the writer: a save must not fail because a live notification did. And the handlers
/// are removed when the subscription is disposed, because the store can outlive the host that installed them (test
/// hosts share one store; a disposed host's handler failed every later save on it).
/// </para>
/// </remarks>
[Register(typeof(ISparkChangeFeed), ServiceLifetime.Singleton)]
internal sealed partial class RavenSparkChangeFeed : ISparkChangeFeed, IDisposable
{
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ILogger<RavenSparkChangeFeed> logger;

    private static readonly ConditionalWeakTable<InMemoryDocumentSessionOperations, List<SparkStoreChange>> PendingDeletes = new();

    private readonly object gate = new();
    private IDatabaseChanges? changes;

    public event Action? Reconnected;

    public IDisposable OnCommitted(Action<SparkStoreChange> handler)
    {
        if (store is not DocumentStoreBase storeBase)
            return new Removal(null);

        EventHandler<AfterSaveChangesEventArgs> afterSave = (_, e) =>
        {
            try
            {
                Flush(e.Session, handler);
                var metadata = e.DocumentMetadata;
                handler(new SparkStoreChange(
                    e.DocumentId,
                    metadata?.TryGetValue(Constants.Documents.Metadata.Collection, out var collection) == true ? collection as string : Collection(e.Entity),
                    metadata?.TryGetValue(Constants.Documents.Metadata.ChangeVector, out var cv) == true ? cv as string : null));
            }
            catch
            {
                // Never the writer's problem; the consumer's revalidation recovers a lost notification.
            }
        };

        EventHandler<BeforeDeleteEventArgs> beforeDelete = (_, e) =>
        {
            try
            {
                if (e.Session is not { } session || string.IsNullOrEmpty(e.DocumentId))
                    return;
                var pending = PendingDeletes.GetOrCreateValue(session);
                lock (pending)
                    pending.Add(new SparkStoreChange(e.DocumentId, Collection(e.Entity), null));
            }
            catch
            {
            }
        };

        EventHandler<SessionDisposingEventArgs> disposing = (_, e) =>
        {
            try
            {
                Flush(e.Session, handler);
            }
            catch
            {
            }
        };

        storeBase.OnAfterSaveChanges += afterSave;
        storeBase.OnBeforeDelete += beforeDelete;
        storeBase.OnSessionDisposing += disposing;
        return new Removal(() =>
        {
            storeBase.OnAfterSaveChanges -= afterSave;
            storeBase.OnBeforeDelete -= beforeDelete;
            storeBase.OnSessionDisposing -= disposing;
        });
    }

    public SparkChangeWatch WatchDocument(string id, Action<SparkStoreChange> handler)
        => Watch(Connection().ForDocument(id), handler);

    public SparkChangeWatch WatchCollection(string collection, Action<SparkStoreChange> handler)
        => Watch(Connection().ForDocumentsInCollection(collection), handler);

    private static SparkChangeWatch Watch(IChangesObservable<DocumentChange> observable, Action<SparkStoreChange> handler)
    {
        var subscription = observable.Subscribe(new Observer(handler));
        return new SparkChangeWatch(subscription, observable.EnsureSubscribedNow());
    }

    /// <summary>One multiplexed Changes connection, opened by the first watch.</summary>
    private IDatabaseChanges Connection()
    {
        lock (gate)
        {
            if (changes is not null)
                return changes;
            changes = store.Changes(store.Database);
            changes.ConnectionStatusChanged += (_, _) =>
            {
                // Notifications are not durable: whatever was missed while disconnected is recovered by the consumer
                // re-evaluating everything once the connection is back.
                if (changes?.Connected == true)
                    Reconnected?.Invoke();
            };
            changes.OnError += ex => logger.LogWarning(ex, "The RavenDB Changes connection failed; it reconnects by itself");
            return changes;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            changes?.Dispose();
            changes = null;
        }
    }

    private string? Collection(object? entity)
        => entity is null ? null : store.Conventions.GetCollectionName(entity);

    private static void Flush(InMemoryDocumentSessionOperations? session, Action<SparkStoreChange> handler)
    {
        if (session is null || !PendingDeletes.TryGetValue(session, out var pending))
            return;
        SparkStoreChange[] flushed;
        lock (pending)
        {
            flushed = [.. pending];
            pending.Clear();
        }
        foreach (var change in flushed)
            handler(change);
    }

    private sealed class Observer(Action<SparkStoreChange> handler) : IObserver<DocumentChange>
    {
        public void OnNext(DocumentChange value)
        {
            try
            {
                handler(new SparkStoreChange(value.Id, value.CollectionName, value.ChangeVector));
            }
            catch
            {
                // Revalidation recovers a lost notification.
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class Removal(Action? remove) : IDisposable
    {
        private Action? remove = remove;
        public void Dispose() => Interlocked.Exchange(ref remove, null)?.Invoke();
    }
}
