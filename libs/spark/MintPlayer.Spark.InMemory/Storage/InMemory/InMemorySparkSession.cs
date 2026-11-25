using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Storage.InMemory;

/// <summary>
/// A unit of work over an <see cref="InMemoryDatabase"/>, with the RavenDB session's semantics: an identity map (one
/// instance per id, query results included), change tracking against the JSON read, writes staged until
/// <see cref="SaveChangesAsync"/>, which applies them atomically, and the same request budget.
/// </summary>
internal sealed class InMemorySparkSession : ISparkSession
{
    private readonly InMemoryDatabase database;
    private readonly Dictionary<string, Tracked> tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<object, string> ids = new(ReferenceEqualityComparer.Instance);

    public InMemorySparkSession(InMemoryDatabase database) => this.database = database;

    /// <summary>
    /// <see cref="Snapshot"/> is the JSON last read or written (null: stored in this session, never committed).
    /// <see cref="ExpectedVersion"/> is the version a write must find, when the caller asked for a check.
    /// </summary>
    private sealed class Tracked(object entity, string? snapshot, string? version, DateTimeOffset? expiresAt = null)
    {
        public object Entity { get; } = entity;
        public string? Snapshot { get; set; } = snapshot;
        public string? Version { get; set; } = version;
        public string? ExpectedVersion { get; set; }
        public bool Deleted { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; } = expiresAt;

        /// <summary>The expiry was set since the last commit: a write even when the entity itself is unchanged.</summary>
        public bool ExpiryChanged { get; set; }
    }

    /// <summary>Deletes by id of documents this session never loaded, with the version they must be at.</summary>
    private readonly Dictionary<string, string?> deletesById = new(StringComparer.OrdinalIgnoreCase);

    public object Native => this;

    public int NumberOfRequests { get; private set; }

    /// <summary>RavenDB's default (30), so a code path that would exhaust a RavenDB session fails here too.</summary>
    public int MaxNumberOfRequests { get; set; } = 30;

    public bool OptimisticConcurrency { get; set; }

    /// <summary>The version a write of <paramref name="row"/> must find: the caller's, else D37's implicit one, else none.</summary>
    private string? Expected(Tracked row)
        => row.ExpectedVersion ?? (OptimisticConcurrency ? (row.Snapshot is null ? string.Empty : row.Version) : null);

    public event Action<object>? BeforeStore;
    public event Action<object>? BeforeDelete;

    private void Request()
    {
        if (++NumberOfRequests > MaxNumberOfRequests)
            throw new InvalidOperationException(
                $"The maximum number of requests ({MaxNumberOfRequests}) allowed for this session has been reached. "
                + "Use SparkSessionExtensions.IgnoreMaxRequests() around a path that needs more.");
    }

    // ---- reads ----

    /// <remarks>
    /// Read when the query runs, not when it is opened, as a RavenDB query is: that is what lets <see cref="StreamAsync{T}"/>
    /// read the rows untracked.
    /// </remarks>
    /// <summary>Reads are always current here: nothing to wait for.</summary>
    public IQueryable<T> Query<T>(TimeSpan waitForIndexes) where T : class => Query<T>();

    public IQueryable<T> Query<T>() where T : class
    {
        Request();
        return Rows<T>(GetCollectionName(typeof(T))).AsQueryable();
    }

    private IEnumerable<T> Rows<T>(string collection) where T : class
    {
        foreach (var entry in database.InCollection(collection))
            if (Materialize(entry, typeof(T)) is T row)
                yield return row;
    }

    /// <summary>While a stream reads its query: the rows it read untracked, with their id and version.</summary>
    private Dictionary<object, (string Id, string Version)>? streaming;

    public async IAsyncEnumerable<SparkStreamResult<T>> StreamAsync<T>(IQueryable<T> query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var read = new Dictionary<object, (string Id, string Version)>(ReferenceEqualityComparer.Instance);
        List<T> rows;
        streaming = read;
        try
        {
            rows = [.. query];
        }
        finally
        {
            streaming = null;
        }

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A row this session already tracked comes back as its tracked instance; any other row is a detached copy.
            var (id, version) = row is null ? default
                : read.TryGetValue(row, out var detached) ? detached
                : ids.TryGetValue(row, out var trackedId) ? (trackedId, tracked[trackedId].Version!)
                : default;
            yield return new SparkStreamResult<T>(id, row, version);
        }
        await Task.CompletedTask;
    }

    /// <remarks>A <see cref="Abstractions.Views.SparkView{TRow}"/> is rendered as LINQ to objects (#536 D32, D42); a native index is not.</remarks>
    public IQueryable<T> Query<T, TIndex>() where T : class where TIndex : class, new()
    {
        if (!typeof(Abstractions.Views.ISparkView).IsAssignableFrom(typeof(TIndex)))
            throw new NotSupportedException(
                $"The in-memory store has no indexes ({typeof(TIndex).Name}). Query the collection with Query<{typeof(T).Name}>() instead.");
        var view = (Abstractions.Views.ISparkView)new TIndex();
        if (view.RowType != typeof(T))
            throw new InvalidOperationException($"View {view.Name} yields {view.RowType.Name}, not {typeof(T).Name}.");
        return (IQueryable<T>)InMemoryViewRenderer.Render(view, this);
    }

    public Task<T?> LoadAsync<T>(string id, CancellationToken cancellationToken = default) where T : class
        => Task.FromResult(LoadOne(id, typeof(T)) as T);

    public Task<IReadOnlyDictionary<string, T?>> LoadAsync<T>(IEnumerable<string> ids, CancellationToken cancellationToken = default) where T : class
        => Task.FromResult<IReadOnlyDictionary<string, T?>>(LoadMany(ids, typeof(T)).ToDictionary(p => p.Key, p => p.Value as T, StringComparer.OrdinalIgnoreCase));

    /// <remarks>Every document is in the process, so an include has nothing to save: the referenced loads are free.</remarks>
    public Task<IReadOnlyDictionary<string, T?>> LoadAsync<T>(IEnumerable<string> ids, IReadOnlyCollection<string> includePaths, CancellationToken cancellationToken = default) where T : class
        => LoadAsync<T>(ids, cancellationToken);

    public Task PreloadAsync(IReadOnlyCollection<(Type EntityType, string Id)> documents, CancellationToken cancellationToken = default)
    {
        if (documents.Count == 0)
            return Task.CompletedTask;
        Request();
        foreach (var (entityType, id) in documents)
            if (!tracked.ContainsKey(id) && database.Get(id) is { } entry)
                Materialize(entry, entityType);
        return Task.CompletedTask;
    }

    public Task<object?> LoadAsync(Type entityType, string id, CancellationToken cancellationToken = default)
        => Task.FromResult(LoadOne(id, entityType));

    public Task<IReadOnlyDictionary<string, object?>> LoadAsync(Type entityType, IEnumerable<string> ids, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<string, object?>>(LoadMany(ids, entityType));

    public Task<IReadOnlyList<T>> LoadStartingWithAsync<T>(string idPrefix, int start = 0, int pageSize = 25, string? startAfter = null, CancellationToken cancellationToken = default) where T : class
    {
        Request();
        IReadOnlyList<T> rows = [.. database.StartingWith(idPrefix)
            .Where(e => startAfter is null || StringComparer.OrdinalIgnoreCase.Compare(e.Id, startAfter) > 0)
            .Skip(start)
            .Take(pageSize)
            .Select(e => Materialize(e, typeof(T)))
            .OfType<T>()];
        return Task.FromResult(rows);
    }

    public Task<bool> ExistsAsync<T>(Id<T> id, CancellationToken cancellationToken = default) where T : class
        => ExistsAsync(typeof(T), id.Value, cancellationToken);

    public Task<bool> ExistsAsync(Type entityType, string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id))
            return Task.FromResult(false);
        if (tracked.TryGetValue(id, out var known))
            return Task.FromResult(!known.Deleted);
        Request();
        return Task.FromResult(database.Get(id) is not null);
    }

    private object? LoadOne(string id, Type type)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        if (tracked.TryGetValue(id, out var known))
            return known.Deleted ? null : Typed(known.Entity, type);
        if (deletesById.ContainsKey(id))
            return null;
        Request();
        return database.Get(id) is { } entry ? Materialize(entry, type) : null;
    }

    private Dictionary<string, object?> LoadMany(IEnumerable<string> requested, Type type)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var missing = requested.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Any(id => !tracked.ContainsKey(id) && !deletesById.ContainsKey(id)))
            Request();
        foreach (var id in missing)
        {
            if (tracked.TryGetValue(id, out var known))
                result[id] = known.Deleted ? null : Typed(known.Entity, type);
            else if (deletesById.ContainsKey(id))
                result[id] = null;
            else
                result[id] = database.Get(id) is { } entry ? Materialize(entry, type) : null;
        }
        return result;
    }

    /// <summary>
    /// The session's instance for <paramref name="entry"/>: the tracked one, or a new one read as the stored type when
    /// that is a <paramref name="requested"/> (a load of a base type keeps the derived instance), else as
    /// <paramref name="requested"/>, as RavenDB reads a document into the type asked for.
    /// </summary>
    private object? Materialize(InMemoryDatabase.Entry entry, Type requested)
    {
        if (tracked.TryGetValue(entry.Id, out var known))
            return known.Deleted ? null : Typed(known.Entity, requested);
        if (deletesById.ContainsKey(entry.Id))
            return null;

        var type = requested.IsAssignableFrom(entry.ClrType) ? entry.ClrType : requested;
        var entity = SparkDocumentJson.Deserialize(entry.Json, type);
        InMemoryConventions.WriteId(entity, entry.Id);
        if (streaming is not null)
        {
            streaming[entity] = (entry.Id, entry.Version);
            return entity;
        }
        tracked[entry.Id] = new Tracked(entity, SparkDocumentJson.Serialize(entity), entry.Version, entry.ExpiresAt);
        ids[entity] = entry.Id;
        return entity;
    }

    private static object? Typed(object entity, Type type) => type.IsInstanceOfType(entity) ? entity : null;

    // ---- writes ----

    public Task StoreAsync(object entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (ids.ContainsKey(entity))
            return Task.CompletedTask;
        var id = InMemoryConventions.ReadId(entity) is { Length: > 0 } own ? own : InMemoryConventions.NewId(entity);
        Track(entity, id, expectedVersion: null);
        return Task.CompletedTask;
    }

    public Task StoreAsync(object entity, string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        Track(entity, id, expectedVersion: null);
        return Task.CompletedTask;
    }

    public Task StoreAsync(object entity, string? expectedVersion, string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        Track(entity, id, expectedVersion);
        return Task.CompletedTask;
    }

    private void Track(object entity, string id, string? expectedVersion)
    {
        if (tracked.TryGetValue(id, out var known))
        {
            if (!ReferenceEquals(known.Entity, entity))
                throw new InvalidOperationException(
                    $"Attempted to associate a different object with id '{id}': the session already tracks one.");
            known.Deleted = false;
            if (expectedVersion is not null)
                known.ExpectedVersion = expectedVersion;
            return;
        }
        if (ids.TryGetValue(entity, out var other) && !string.Equals(other, id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The entity is already tracked under id '{other}'.");

        deletesById.Remove(id);
        InMemoryConventions.WriteId(entity, id);
        tracked[id] = new Tracked(entity, snapshot: null, version: null) { ExpectedVersion = expectedVersion };
        ids[entity] = id;
    }

    public void Delete<T>(Id<T> id, string? expectedVersion = null) where T : class
        => Delete(typeof(T), id.Value, expectedVersion);

    public void Delete(Type entityType, string id, string? expectedVersion = null)
    {
        if (tracked.TryGetValue(id, out var known))
        {
            known.Deleted = true;
            if (expectedVersion is not null)
                known.ExpectedVersion = expectedVersion;
            return;
        }
        deletesById[id] = expectedVersion;
    }

    public void Delete(object entity)
    {
        // A string here is an id meant for Delete(Type, id): the untyped Delete(string) is gone (#536 D27).
        if (entity is string)
            throw new ArgumentException("Delete(object) takes an entity; delete by id with Delete<T>(Id<T>) or Delete(Type, string).", nameof(entity));
        if (!ids.TryGetValue(entity, out var id))
            throw new InvalidOperationException($"{entity.GetType().Name} is not associated with the session; it cannot be deleted.");
        Delete(entity.GetType(), id);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var writes = new List<InMemoryDatabase.Write>();
        var stored = new List<(Tracked Row, string Json)>();
        foreach (var (id, row) in tracked)
        {
            if (row.Deleted)
            {
                // Like RavenDB's OnBeforeDelete: raised by the commit, for the entities it deletes.
                BeforeDelete?.Invoke(row.Entity);
                if (row.Snapshot is not null || row.ExpectedVersion is not null)
                    writes.Add(new InMemoryDatabase.Remove(id, Expected(row)));
                continue;
            }
            if (row.Snapshot is not null && row.ExpectedVersion is null && !row.ExpiryChanged && SparkDocumentJson.Serialize(row.Entity) == row.Snapshot)
                continue;

            // Like RavenDB's OnBeforeStore: the hook may still change the entity, so it serializes afterwards.
            BeforeStore?.Invoke(row.Entity);
            var json = SparkDocumentJson.Serialize(row.Entity);
            writes.Add(new InMemoryDatabase.Put(id, Expected(row), json, row.Entity.GetType(), GetCollectionName(row.Entity.GetType()), row.ExpiresAt));
            stored.Add((row, json));
        }
        foreach (var (id, expected) in deletesById)
            writes.Add(new InMemoryDatabase.Remove(id, expected));
        writes.AddRange(patches);

        if (writes.Count == 0 && reservations.Count == 0)
            return Task.CompletedTask;

        Request();
        var versions = database.Commit(writes, reservations);
        reservations.Clear();
        patches.Clear();

        foreach (var (row, json) in stored)
        {
            row.Snapshot = json;
            row.Version = versions[ids[row.Entity]];
            row.ExpectedVersion = null;
            row.ExpiryChanged = false;
        }
        foreach (var id in tracked.Where(p => p.Value.Deleted).Select(p => p.Key).ToList())
        {
            ids.Remove(tracked[id].Entity);
            tracked.Remove(id);
        }
        deletesById.Clear();
        return Task.CompletedTask;
    }

    /// <summary>Appends staged for the next commit, which applies them to the stored JSON under the database's lock.</summary>
    private readonly List<InMemoryDatabase.Patch> patches = [];

    public void PatchAppend<T, TItem>(string id, System.Linq.Expressions.Expression<Func<T, IEnumerable<TItem>>> list, TItem item) where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var read = list.Compile();
        patches.Add(new InMemoryDatabase.Patch(id, entry =>
        {
            var row = Read<T>(entry) ?? throw new InvalidOperationException($"Document '{id}' is not a {typeof(T).Name}.");
            var target = read(row) as ICollection<TItem>
                ?? throw new InvalidOperationException($"Cannot append to {list} of '{id}': the list is null or fixed-size.");
            target.Add(item);
            return SparkDocumentJson.Serialize(row);
        }));
    }

    // ---- set-based ----

    public Task<long> DeleteWhereAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) where T : class
    {
        var matches = predicate.Compile();
        Request();
        return Task.FromResult(database.Rewrite(GetCollectionName(typeof(T)),
            entry => Read<T>(entry) is { } row && matches(row) ? InMemoryDatabase.Deleted : null));
    }

    public Task<long> UpdateWhereAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate, Action<SparkSetters<T>> set, CancellationToken cancellationToken = default) where T : class
    {
        var setters = new SparkSetters<T>();
        set(setters);
        var matches = predicate.Compile();
        Request();
        return Task.FromResult(database.Rewrite(GetCollectionName(typeof(T)), entry =>
        {
            // Read as the stored type (Read<T>), so the rewritten JSON keeps a derived type's members.
            if (Read<T>(entry) is not { } row || !matches(row))
                return null;
            foreach (var setter in setters.Setters)
                setter.ApplyTo(row);
            return SparkDocumentJson.Serialize(row);
        }));
    }

    /// <summary>A stored row as <typeparamref name="T"/>, detached from the session.</summary>
    private static T? Read<T>(InMemoryDatabase.Entry entry) where T : class
    {
        var type = typeof(T).IsAssignableFrom(entry.ClrType) ? entry.ClrType : typeof(T);
        var row = SparkDocumentJson.Deserialize(entry.Json, type);
        InMemoryConventions.WriteId(row, entry.Id);
        return row as T;
    }

    // ---- reservations ----

    /// <summary>The claims and releases the next commit applies, in order.</summary>
    private readonly List<InMemoryDatabase.Reservation> reservations = [];

    public void Reserve(string key, string ownerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerId);
        reservations.Add(new InMemoryDatabase.Reservation(key, ownerId, Claim: true));
    }

    public void Release(string key, string ownerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerId);
        reservations.Add(new InMemoryDatabase.Reservation(key, ownerId, Claim: false));
    }

    public Task<string?> GetReservationAsync(string key, CancellationToken cancellationToken = default)
    {
        Request();
        return Task.FromResult(database.ReservationOwner(key));
    }

    // ---- metadata ----

    public void SetExpiry(object entity, DateTimeOffset? expiresAt)
    {
        var row = TrackedRow(entity);
        row.ExpiresAt = expiresAt?.ToUniversalTime();
        row.ExpiryChanged = true;
    }

    public DateTimeOffset? GetExpiry(object entity) => TrackedRow(entity).ExpiresAt;

    private Tracked TrackedRow(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return ids.TryGetValue(entity, out var id) && tracked.TryGetValue(id, out var row)
            ? row
            : throw new InvalidOperationException($"{entity.GetType().Name} is not associated with the session; store or load it first.");
    }

    public string? GetId(object entity) => ids.GetValueOrDefault(entity);

    public string? GetVersion(object entity)
        => ids.TryGetValue(entity, out var id) && tracked.TryGetValue(id, out var row) ? row.Version : null;

    public Task<IReadOnlyDictionary<string, string>> GetVersionsAsync<T>(IEnumerable<Id<T>> ids, CancellationToken cancellationToken = default) where T : class
        => GetVersionsAsync(typeof(T), ids.Select(id => id.Value), cancellationToken);

    public Task<IReadOnlyDictionary<string, string>> GetVersionsAsync(Type entityType, IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (database.Get(id) is { } entry)
                result[entry.Id] = entry.Version;
        return Task.FromResult<IReadOnlyDictionary<string, string>>(result);
    }

    public string GetCollectionName(Type type) => InMemoryConventions.GetCollectionName(type);

    public string? GetStoredCollectionName(object entity)
        => ids.TryGetValue(entity, out var id) && database.Get(id) is { } entry ? entry.Collection : null;

    public bool IsLoaded(string id) => tracked.ContainsKey(id);

    public bool HasChanged(object entity)
        => !ids.TryGetValue(entity, out var id)
            || tracked[id] is not { } row
            || row.Snapshot is null
            || row.Deleted
            || row.ExpiryChanged
            || SparkDocumentJson.Serialize(entity) != row.Snapshot;

    public IReadOnlyCollection<SparkTrackedEntity> TrackedEntities
        => [.. tracked.Values.Select(row => new SparkTrackedEntity(row.Entity, row.Deleted))];

    public void Evict(object entity)
    {
        if (ids.Remove(entity, out var id))
            tracked.Remove(id);
    }

    public void Clear()
    {
        tracked.Clear();
        ids.Clear();
        deletesById.Clear();
        reservations.Clear();
        patches.Clear();
    }

    public Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default) => Task.FromResult(query.ToList());

    public Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default) => Task.FromResult(query.Count());

    public void Dispose() => Clear();
}
