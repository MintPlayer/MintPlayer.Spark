using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Live;
using Raven.Client.Documents;
using Raven.Client.Documents.Changes;

namespace MintPlayer.Spark.Live;

/// <summary>One committed change, as a dirty bit: never its content.</summary>
/// <param name="DocumentId">The document, when known.</param>
/// <param name="Collection">Its collection, when known.</param>
/// <param name="ChangeVector">The change vector the write produced, when known (de-duplication key under <c>Both</c>).</param>
/// <param name="FromChanges">Whether it came from the RavenDB Changes API rather than this process.</param>
internal readonly record struct LiveChange(string? DocumentId, string? Collection, string? ChangeVector, bool FromChanges);

/// <summary>
/// The process-wide side of live updates (X4): which subscription watches which document or collection,
/// the RavenDB Changes watches those need (ref-counted, one multiplexed connection), de-duplication of the
/// two sources under <c>Both</c> (D33), and the per-user socket count (D36).
/// </summary>
/// <remarks>
/// A change only marks subscriptions dirty. Each one re-runs the HTTP read path under its own viewer's
/// rights (<see cref="LiveConnection"/>), so a change to a row a viewer cannot see produces no frame for
/// them: there is nothing to diff.
/// </remarks>
internal sealed class LiveHub : ISparkLiveNotifier, IDisposable
{
    /// <summary>How long a seen (id, change vector) pair suppresses its echo from the other source.</summary>
    internal static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(5);

    private readonly IDocumentStore store;
    private readonly IOptionsMonitor<SparkLiveOptions> options;
    private readonly ILogger<LiveHub> logger;
    private readonly TimeProvider time;

    private readonly object gate = new();
    private readonly Dictionary<string, HashSet<LiveSubscription>> byDocument = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<LiveSubscription>> byCollection = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<LiveSubscription> all = [];
    private readonly Dictionary<string, IDisposable> watches = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> sockets = new(StringComparer.Ordinal);
    private IDatabaseChanges? changes;
    private IDisposable? actionsSource;
    private long published, deduplicated;

    public LiveHub(IDocumentStore store, IOptionsMonitor<SparkLiveOptions> options, ILogger<LiveHub> logger, TimeProvider? time = null)
    {
        this.store = store;
        this.options = options;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Changes dispatched, and changes dropped as the other source's echo (for the spike numbers).</summary>
    internal (long Published, long Deduplicated) Counters => (Interlocked.Read(ref published), Interlocked.Read(ref deduplicated));

    private SparkLiveSource Source => options.CurrentValue.Source;
    private bool WatchesChanges => Source is SparkLiveSource.Changes or SparkLiveSource.Both;

    // ---- ISparkLiveNotifier: explicit calls are intent, honoured under every source ----------------------

    public void Changed(string documentId, string? collection = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);
        Dispatch(new LiveChange(documentId, collection, null, FromChanges: false));
    }

    public void CollectionChanged(string collection)
    {
        ArgumentException.ThrowIfNullOrEmpty(collection);
        Dispatch(new LiveChange(null, collection, null, FromChanges: false));
    }

    // ---- sources --------------------------------------------------------------------------------------------

    /// <summary>
    /// A change seen by a source. Under <c>Both</c> the same write arrives twice — first from this process's
    /// store event, 4–22 ms later from the Changes API — and the second is dropped by id + change vector.
    /// </summary>
    public void Publish(LiveChange change)
    {
        if (change.FromChanges ? !WatchesChanges : Source == SparkLiveSource.Changes)
            return;

        if (change.DocumentId is { } id && change.ChangeVector is { } cv)
        {
            var now = time.GetTimestamp();
            var key = id + "\n" + cv;
            if (seen.TryGetValue(key, out var at) && time.GetElapsedTime(at, now) < DedupeWindow)
            {
                Interlocked.Increment(ref deduplicated);
                return;
            }
            seen[key] = now;
            if (seen.Count > 4096)
                Sweep(now);
        }

        Dispatch(change);
    }

    private void Sweep(long now)
    {
        foreach (var (key, at) in seen)
            if (time.GetElapsedTime(at, now) >= DedupeWindow)
                seen.TryRemove(key, out _);
    }

    private void Dispatch(LiveChange change)
    {
        Interlocked.Increment(ref published);
        List<(LiveSubscription Sub, string? Collection)> touched = [];
        lock (gate)
        {
            if (change.DocumentId is { } id && byDocument.TryGetValue(id, out var forDocument))
                foreach (var sub in forDocument)
                    touched.Add((sub, null));
            if (change.Collection is { } collection && byCollection.TryGetValue(collection, out var forCollection))
                foreach (var sub in forCollection)
                    touched.Add((sub, collection));
        }

        foreach (var (sub, collection) in touched)
            sub.MarkDirty(collection);
    }

    /// <summary>Every subscription re-evaluates (the Changes connection came back: notifications were lost).</summary>
    internal void MarkAllDirty()
    {
        List<LiveSubscription> subs;
        lock (gate)
            subs = [.. all];
        foreach (var sub in subs)
            sub.MarkDirty(null);
    }

    // ---- registrations ---------------------------------------------------------------------------------------

    /// <summary>
    /// Starts routing changes to <paramref name="subscription"/>. Completes when every new Changes watch is
    /// confirmed by the server (bounded), so the first evaluation that follows cannot miss a write that lands
    /// between it and the watch.
    /// </summary>
    public async Task RegisterAsync(LiveSubscription subscription)
    {
        List<Task> confirmations = [];
        lock (gate)
        {
            all.Add(subscription);
            foreach (var id in subscription.Documents)
                Add(byDocument, id, subscription, "doc:", confirmations);
            foreach (var collection in subscription.Collections)
                Add(byCollection, collection, subscription, "col:", confirmations);
        }

        if (confirmations.Count == 0)
            return;
        try
        {
            await Task.WhenAll(confirmations).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A RavenDB Changes watch for live updates was not confirmed; the subscription relies on this process's writes and revalidation until it is");
        }
    }

    public void Unregister(LiveSubscription subscription)
    {
        lock (gate)
        {
            if (!all.Remove(subscription))
                return;
            foreach (var id in subscription.Documents)
                Remove(byDocument, id, subscription, "doc:");
            foreach (var collection in subscription.Collections)
                Remove(byCollection, collection, subscription, "col:");
        }
    }

    private void Add(Dictionary<string, HashSet<LiveSubscription>> map, string key, LiveSubscription sub, string kind, List<Task> confirmations)
    {
        if (!map.TryGetValue(key, out var set))
        {
            map[key] = set = [];
            if (WatchesChanges && Watch(kind + key, kind, key) is { } confirmation)
                confirmations.Add(confirmation);
        }
        set.Add(sub);
    }

    private void Remove(Dictionary<string, HashSet<LiveSubscription>> map, string key, LiveSubscription sub, string kind)
    {
        if (!map.TryGetValue(key, out var set) || !set.Remove(sub) || set.Count > 0)
            return;
        map.Remove(key);
        if (watches.Remove(kind + key, out var watch))
            watch.Dispose();
    }

    /// <summary>Only what someone watches: a document or a collection, never every document (D33).</summary>
    private Task? Watch(string watchKey, string kind, string key)
    {
        try
        {
            var connection = Connection();
            var observable = kind == "doc:" ? connection.ForDocument(key) : connection.ForDocumentsInCollection(key);
            watches[watchKey] = observable.Subscribe(new Observer(this));
            return observable.EnsureSubscribedNow();
        }
        catch (Exception ex)
        {
            // The Changes API is the cross-process half; this process's own writes still arrive.
            logger.LogWarning(ex, "Live updates could not watch {Key} through the RavenDB Changes API; only this process's writes will be seen for it", watchKey);
            return null;
        }
    }

    private IDatabaseChanges Connection()
    {
        if (changes is not null)
            return changes;
        changes = store.Changes(store.Database);
        changes.ConnectionStatusChanged += (_, _) =>
        {
            // Notifications are not durable: whatever was missed while disconnected is recovered by
            // re-evaluating everything once the connection is back.
            if (changes?.Connected == true)
                MarkAllDirty();
        };
        changes.OnError += ex => logger.LogWarning(ex, "The RavenDB Changes connection used by live updates failed; it reconnects by itself");
        return changes;
    }

    private sealed class Observer(LiveHub hub) : IObserver<DocumentChange>
    {
        public void OnNext(DocumentChange value)
        {
            try
            {
                hub.Publish(new LiveChange(value.Id, value.CollectionName, value.ChangeVector, FromChanges: true));
            }
            catch
            {
                // Revalidate recovers a lost notification.
            }
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    // ---- sockets per user (D36) -------------------------------------------------------------------------------

    public bool TryAcquireSocket(string userKey)
    {
        var max = options.CurrentValue.MaxSocketsPerUser;
        while (true)
        {
            var current = sockets.GetOrAdd(userKey, 0);
            if (current >= max)
                return false;
            if (sockets.TryUpdate(userKey, current + 1, current))
                return true;
        }
    }

    public void ReleaseSocket(string userKey)
    {
        while (sockets.TryGetValue(userKey, out var current))
        {
            if (current <= 1 ? sockets.TryRemove(new KeyValuePair<string, int>(userKey, current)) : sockets.TryUpdate(userKey, current - 1, current))
                return;
        }
    }

    /// <summary>The in-process source's store handlers, removed when this hub goes (the store may outlive the host).</summary>
    internal void AttachActionsSource(IDisposable source) => actionsSource = source;

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var watch in watches.Values)
                watch.Dispose();
            watches.Clear();
        }
        Interlocked.Exchange(ref actionsSource, null)?.Dispose();
        changes?.Dispose();
    }
}
