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
    private sealed class Tracked(object entity, string? snapshot, string? version)
    {
        public object Entity { get; } = entity;
        public string? Snapshot { get; set; } = snapshot;
        public string? Version { get; set; } = version;
        public string? ExpectedVersion { get; set; }
        public bool Deleted { get; set; }
    }

    /// <summary>Deletes by id of documents this session never loaded, with the version they must be at.</summary>
    private readonly Dictionary<string, string?> deletesById = new(StringComparer.OrdinalIgnoreCase);

    public object Native => this;

    public int NumberOfRequests { get; private set; }

    /// <summary>RavenDB's default (30), so a code path that would exhaust a RavenDB session fails here too.</summary>
    public int MaxNumberOfRequests { get; set; } = 30;

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

    public IQueryable<T> Query<T>() where T : class
    {
        Request();
        var rows = new List<T>();
        foreach (var entry in database.InCollection(GetCollectionName(typeof(T))))
            if (Materialize(entry, typeof(T)) is T row)
                rows.Add(row);
        return rows.AsQueryable();
    }

    public IQueryable<T> Query<T, TIndex>() where T : class where TIndex : class, new()
        => throw new NotSupportedException(
            $"The in-memory store has no indexes ({typeof(TIndex).Name}). Query the collection with Query<{typeof(T).Name}>() instead.");

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

    public Task<IReadOnlyList<T>> LoadStartingWithAsync<T>(string idPrefix, int start = 0, int pageSize = 25, CancellationToken cancellationToken = default) where T : class
    {
        Request();
        IReadOnlyList<T> rows = [.. database.StartingWith(idPrefix)
            .Skip(start)
            .Take(pageSize)
            .Select(e => Materialize(e, typeof(T)))
            .OfType<T>()];
        return Task.FromResult(rows);
    }

    public Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default)
    {
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
        var entity = InMemoryJson.Deserialize(entry.Json, type);
        InMemoryConventions.WriteId(entity, entry.Id);
        tracked[entry.Id] = new Tracked(entity, InMemoryJson.Serialize(entity), entry.Version);
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

    public void Delete(string id, string? expectedVersion = null)
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
        if (!ids.TryGetValue(entity, out var id))
            throw new InvalidOperationException($"{entity.GetType().Name} is not associated with the session; it cannot be deleted.");
        Delete(id);
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
                    writes.Add(new InMemoryDatabase.Remove(id, row.ExpectedVersion));
                continue;
            }
            if (row.Snapshot is not null && row.ExpectedVersion is null && InMemoryJson.Serialize(row.Entity) == row.Snapshot)
                continue;

            // Like RavenDB's OnBeforeStore: the hook may still change the entity, so it serializes afterwards.
            BeforeStore?.Invoke(row.Entity);
            var json = InMemoryJson.Serialize(row.Entity);
            writes.Add(new InMemoryDatabase.Put(id, row.ExpectedVersion, json, row.Entity.GetType(), GetCollectionName(row.Entity.GetType())));
            stored.Add((row, json));
        }
        foreach (var (id, expected) in deletesById)
            writes.Add(new InMemoryDatabase.Remove(id, expected));

        if (writes.Count == 0)
            return Task.CompletedTask;

        Request();
        var versions = database.Commit(writes);

        foreach (var (row, json) in stored)
        {
            row.Snapshot = json;
            row.Version = versions[ids[row.Entity]];
            row.ExpectedVersion = null;
        }
        foreach (var id in tracked.Where(p => p.Value.Deleted).Select(p => p.Key).ToList())
        {
            ids.Remove(tracked[id].Entity);
            tracked.Remove(id);
        }
        deletesById.Clear();
        return Task.CompletedTask;
    }

    // ---- metadata ----

    public string? GetId(object entity) => ids.GetValueOrDefault(entity);

    public string? GetVersion(object entity)
        => ids.TryGetValue(entity, out var id) && tracked.TryGetValue(id, out var row) ? row.Version : null;

    public Task<IReadOnlyDictionary<string, string>> GetVersionsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
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
            || InMemoryJson.Serialize(entity) != row.Snapshot;

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
    }

    public Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default) => Task.FromResult(query.ToList());

    public Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default) => Task.FromResult(query.Count());

    public void Dispose() => Clear();
}
