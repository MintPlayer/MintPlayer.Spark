using System.Collections.Concurrent;
using System.Reflection;
using MintPlayer.Spark.Abstractions.Storage;
using Raven.Client.Documents;
using Raven.Client.Documents.Commands;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Sparrow.Json;

namespace MintPlayer.Spark.Storage.Raven;

/// <summary>
/// <see cref="ISparkSession"/> over a RavenDB <see cref="IAsyncDocumentSession"/>. The request-scoped instance
/// wraps the request's own Raven session, so code still holding <c>IAsyncDocumentSession</c> shares the unit of work.
/// </summary>
internal sealed class RavenSparkSession : ISparkSession
{
    private readonly bool ownsSession;

    public RavenSparkSession(IAsyncDocumentSession session, bool ownsSession = false)
    {
        Session = session;
        this.ownsSession = ownsSession;
    }

    private Action<object>? beforeStore;
    private Action<object>? beforeDelete;

    // Hooked onto Raven's events only while someone listens: AsSparkSession() wraps a session ad hoc, and a
    // handler left behind by every wrapper would fire on each later SaveChanges.
    private void OnRavenBeforeStore(object? sender, BeforeStoreEventArgs e) => beforeStore?.Invoke(e.Entity);

    private void OnRavenBeforeDelete(object? sender, BeforeDeleteEventArgs e) => beforeDelete?.Invoke(e.Entity);

    public IAsyncDocumentSession Session { get; }

    public object Native => Session;

    private IDocumentStore Store => Session.Advanced.DocumentStore;

    public IQueryable<T> Query<T>() where T : class => Session.Query<T>();

    public Task<T?> LoadAsync<T>(string id, CancellationToken cancellationToken = default) where T : class
        => Session.LoadAsync<T>(id, cancellationToken)!;

    public async Task<IReadOnlyDictionary<string, T?>> LoadAsync<T>(IEnumerable<string> ids, CancellationToken cancellationToken = default) where T : class
        => (await Session.LoadAsync<T>(ids, cancellationToken))!;

    public async Task<object?> LoadAsync(Type entityType, string id, CancellationToken cancellationToken = default)
    {
        var task = (Task)LoadOne.GetOrAdd(entityType, static t => LoadOneOpen.MakeGenericMethod(t))
            .Invoke(Session, [id, cancellationToken])!;
        await task;
        return task.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(task);
    }

    public async Task<IReadOnlyDictionary<string, object?>> LoadAsync(Type entityType, IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var task = (Task)LoadMany.GetOrAdd(entityType, static t => LoadManyOpen.MakeGenericMethod(t))
            .Invoke(Session, [ids, cancellationToken])!;
        await task;
        var loaded = (System.Collections.IDictionary)task.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(task)!;
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in loaded)
            result[(string)entry.Key] = entry.Value;
        return result;
    }

    public async Task<IReadOnlyList<T>> LoadStartingWithAsync<T>(string idPrefix, int start = 0, int pageSize = 25, CancellationToken cancellationToken = default) where T : class
        => [.. await Session.Advanced.LoadStartingWithAsync<T>(idPrefix, null, start, pageSize, token: cancellationToken)];

    public Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default)
        => Session.Advanced.ExistsAsync(id, cancellationToken);

    public Task StoreAsync(object entity, CancellationToken cancellationToken = default)
        => Session.StoreAsync(entity, cancellationToken);

    public Task StoreAsync(object entity, string id, CancellationToken cancellationToken = default)
        => Session.StoreAsync(entity, id, cancellationToken);

    public Task StoreAsync(object entity, string? expectedVersion, string id, CancellationToken cancellationToken = default)
        => Session.StoreAsync(entity, expectedVersion, id, cancellationToken);

    public void Delete(string id, string? expectedVersion = null)
    {
        if (expectedVersion is null)
            Session.Delete(id);
        else
            Session.Delete(id, expectedVersion);
    }

    public void Delete(object entity) => Session.Delete(entity);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Session.SaveChangesAsync(cancellationToken);
        }
        catch (global::Raven.Client.Exceptions.ConcurrencyException ex)
        {
            throw new SparkStoreConcurrencyException("A document changed since the write expected it.", ex);
        }
    }

    public string? GetId(object entity) => Session.Advanced.GetDocumentId(entity);

    public string? GetVersion(object entity) => Session.Advanced.GetChangeVectorFor(entity);

    /// <remarks>
    /// Read as metadata only, in one request, for every row shape alike — entities, index projections,
    /// custom and streamed queries. A projection is not tracked by the session, so
    /// <c>GetChangeVectorFor</c> throws on it (spike S5b); a lookup by id covers every path in one place.
    /// The grid's row etags (#467, D14) come from here, read right after the query: an edit landing between
    /// the two reads hands the grid a newer etag than its values — a window of one round trip, against the
    /// minutes a grid stays open, which is the race D14 closes.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string>> GetVersionsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var distinct = ids.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (distinct.Length == 0)
            return result;

        var executor = Session.Advanced.RequestExecutor;
        var command = new GetDocumentsCommand(Store.Conventions, distinct, includes: null, metadataOnly: true);
        using (executor.ContextPool.AllocateOperationContext(out JsonOperationContext context))
        {
            await executor.ExecuteAsync(command, context, sessionInfo: null, cancellationToken);
            var results = command.Result?.Results;
            if (results is null)
                return result;

            foreach (var item in results)
            {
                if (item is not BlittableJsonReaderObject document
                    || !document.TryGet(global::Raven.Client.Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata)
                    || !metadata.TryGet(global::Raven.Client.Constants.Documents.Metadata.Id, out string id)
                    || !metadata.TryGet(global::Raven.Client.Constants.Documents.Metadata.ChangeVector, out string changeVector))
                    continue;
                result[id] = changeVector;
            }
        }

        return result;
    }

    public string GetCollectionName(Type type) => Store.Conventions.GetCollectionName(type);

    public string? GetStoredCollectionName(object entity)
        => Session.Advanced.GetMetadataFor(entity).TryGetValue(global::Raven.Client.Constants.Documents.Metadata.Collection, out var actual)
            ? actual?.ToString()
            : null;

    public bool IsLoaded(string id) => Session.Advanced.IsLoaded(id);

    public bool HasChanged(object entity) => Session.Advanced.HasChanged(entity);

    public IReadOnlyCollection<SparkTrackedEntity> TrackedEntities
        => [.. Session.Advanced.GetTrackedEntities().Values.Where(info => info.Entity is not null).Select(info => new SparkTrackedEntity(info.Entity, info.IsDeleted))];

    public void Evict(object entity) => Session.Advanced.Evict(entity);

    public void Clear() => Session.Advanced.Clear();

    public event Action<object>? BeforeStore
    {
        add
        {
            if (beforeStore is null)
                Session.Advanced.OnBeforeStore += OnRavenBeforeStore;
            beforeStore += value;
        }
        remove
        {
            beforeStore -= value;
            if (beforeStore is null)
                Session.Advanced.OnBeforeStore -= OnRavenBeforeStore;
        }
    }

    public event Action<object>? BeforeDelete
    {
        add
        {
            if (beforeDelete is null)
                Session.Advanced.OnBeforeDelete += OnRavenBeforeDelete;
            beforeDelete += value;
        }
        remove
        {
            beforeDelete -= value;
            if (beforeDelete is null)
                Session.Advanced.OnBeforeDelete -= OnRavenBeforeDelete;
        }
    }

    public int NumberOfRequests => Session.Advanced.NumberOfRequests;

    public int MaxNumberOfRequests
    {
        get => Session.Advanced.MaxNumberOfRequestsPerSession;
        set => Session.Advanced.MaxNumberOfRequestsPerSession = value;
    }

    /// <remarks>
    /// A custom query may hand back an in-memory sequence (<c>AsQueryable()</c>), which has no async form; only a
    /// RavenDB query goes over the wire.
    /// </remarks>
    public async Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
        => query is IRavenQueryable<T> raven ? await raven.ToListAsync(cancellationToken) : [.. query];

    public async Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
        => query is IRavenQueryable<T> raven ? await raven.CountAsync(cancellationToken) : query.Count();

    public void Dispose()
    {
        if (ownsSession)
            Session.Dispose();
    }

    private static readonly MethodInfo LoadOneOpen = typeof(IAsyncDocumentSession).GetMethod(
        nameof(IAsyncDocumentSession.LoadAsync), 1, [typeof(string), typeof(CancellationToken)])!;

    private static readonly MethodInfo LoadManyOpen = typeof(IAsyncDocumentSession).GetMethod(
        nameof(IAsyncDocumentSession.LoadAsync), 1, [typeof(IEnumerable<string>), typeof(CancellationToken)])!;

    private static readonly ConcurrentDictionary<Type, MethodInfo> LoadOne = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> LoadMany = new();
}
