using System.Runtime.CompilerServices;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Live;

/// <summary>
/// The <c>Actions</c> change source (D33): every write committed through a RavenDB session of this process —
/// the persistent-object pipeline, custom actions, message handlers, jobs — seen from the document store's own
/// events, so no app code calls anything (D34).
/// </summary>
/// <remarks>
/// <para>
/// Store-level, not an <c>IAfterSaveCommitted</c> interceptor: that one is durable and needs Spark Messaging
/// (a startup error without it), and sees only the pipeline. A live push needs neither durability (a missed
/// notification is recovered by <see cref="SparkLiveOptions.Revalidate"/>) nor the pipeline.
/// </para>
/// <para>
/// A stored document is reported from <c>OnAfterSaveChanges</c>, after the commit, with its new change
/// vector. A delete raises no after-commit event in the RavenDB client, so it is remembered from
/// <c>OnBeforeDelete</c> and reported when that session next commits a store, or when it is disposed. A
/// delete whose save then failed is a spurious dirty bit, which costs one re-evaluation and no frame.
/// Patches, bulk inserts and deletes by id that were never loaded raise no event at all: under
/// <c>Source = Actions</c> code using them calls <see cref="Abstractions.Live.ISparkLiveNotifier"/>.
/// </para>
/// <para>
/// ⚠️ A handler never throws into the writer: a save must not fail because a live notification did. And the
/// handlers are removed when the hub is disposed, because the store can outlive the host that installed them
/// (test hosts share one store; a disposed host's handler failed every later save on it).
/// </para>
/// </remarks>
internal static class LiveActionsSource
{
    private static readonly ConditionalWeakTable<InMemoryDocumentSessionOperations, List<LiveChange>> PendingDeletes = new();

    /// <summary>Installs the handlers; disposing the result removes them.</summary>
    public static IDisposable Install(IDocumentStore store, LiveHub hub)
    {
        if (store is not DocumentStoreBase storeBase)
            return new Removal(null);

        EventHandler<AfterSaveChangesEventArgs> afterSave = (_, e) =>
        {
            try
            {
                Flush(e.Session, hub);
                var metadata = e.DocumentMetadata;
                hub.Publish(new LiveChange(
                    e.DocumentId,
                    metadata?.TryGetValue(Constants.Documents.Metadata.Collection, out var collection) == true ? collection as string : Collection(store, e.Entity),
                    metadata?.TryGetValue(Constants.Documents.Metadata.ChangeVector, out var cv) == true ? cv as string : null,
                    FromChanges: false));
            }
            catch
            {
                // Never the writer's problem; Revalidate recovers a lost notification.
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
                    pending.Add(new LiveChange(e.DocumentId, Collection(store, e.Entity), null, FromChanges: false));
            }
            catch
            {
            }
        };

        EventHandler<SessionDisposingEventArgs> disposing = (_, e) =>
        {
            try
            {
                Flush(e.Session, hub);
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

    private static string? Collection(IDocumentStore store, object? entity)
        => entity is null ? null : store.Conventions.GetCollectionName(entity);

    private static void Flush(InMemoryDocumentSessionOperations? session, LiveHub hub)
    {
        if (session is null || !PendingDeletes.TryGetValue(session, out var pending))
            return;
        LiveChange[] changes;
        lock (pending)
        {
            changes = [.. pending];
            pending.Clear();
        }
        foreach (var change in changes)
            hub.Publish(change);
    }

    private sealed class Removal(Action? remove) : IDisposable
    {
        private Action? remove = remove;
        public void Dispose() => Interlocked.Exchange(ref remove, null)?.Invoke();
    }
}
