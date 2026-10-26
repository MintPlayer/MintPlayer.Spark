namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// A unit of work over the application's store, with an identity map: a provider's session
/// (RavenDB's <c>IAsyncDocumentSession</c>, the in-memory provider's own). It is the seam BELOW Spark's
/// policy pipeline (docs/spark_editor_PRD.md D2/D3) — nothing here checks a permission, a row rule or an
/// interceptor; <c>DatabaseAccess</c> does all of that and calls this for the storage operations only.
/// </summary>
/// <remarks>
/// <para>
/// Queries are plain <see cref="IQueryable{T}"/> (D15). Standard LINQ composes on them and reaches the
/// provider's own query translator, because the runtime object stays the provider's. Execute them through
/// <see cref="ToListAsync{T}"/> / <see cref="CountAsync{T}"/>, never a synchronous enumeration: RavenDB's async
/// session refuses one (spike S9 T2).
/// </para>
/// <para>
/// Versions are opaque strings (RavenDB's change vector). An empty expected version on a store means "must
/// not exist yet"; a mismatch at commit throws the provider's concurrency exception, which Spark reports as
/// a 409.
/// </para>
/// </remarks>
public interface ISparkSession : IDisposable
{
    /// <summary>The collection of <typeparamref name="T"/> as a queryable.</summary>
    IQueryable<T> Query<T>() where T : class;

    Task<T?> LoadAsync<T>(string id, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Loads by id; a missing id maps to null. Keys are the ids asked for.</summary>
    Task<IReadOnlyDictionary<string, T?>> LoadAsync<T>(IEnumerable<string> ids, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Untyped <see cref="LoadAsync{T}(string, CancellationToken)"/>, for callers that hold a <see cref="Type"/>.</summary>
    Task<object?> LoadAsync(Type entityType, string id, CancellationToken cancellationToken = default);

    /// <summary>Untyped <see cref="LoadAsync{T}(IEnumerable{string}, CancellationToken)"/>.</summary>
    Task<IReadOnlyDictionary<string, object?>> LoadAsync(Type entityType, IEnumerable<string> ids, CancellationToken cancellationToken = default);

    /// <summary>Documents whose id starts with <paramref name="idPrefix"/>, in id order.</summary>
    Task<IReadOnlyList<T>> LoadStartingWithAsync<T>(string idPrefix, int start = 0, int pageSize = 25, CancellationToken cancellationToken = default) where T : class;

    Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Tracks <paramref name="entity"/> for the next commit; the provider assigns an id when it has none.</summary>
    Task StoreAsync(object entity, CancellationToken cancellationToken = default);

    Task StoreAsync(object entity, string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tracks <paramref name="entity"/> under <paramref name="id"/>, committed only when the stored version is
    /// still <paramref name="expectedVersion"/> (empty: only when nothing is stored there yet; null: no check).
    /// </summary>
    Task StoreAsync(object entity, string? expectedVersion, string id, CancellationToken cancellationToken = default);

    /// <summary>Deletes <paramref name="id"/> at the next commit, checked against <paramref name="expectedVersion"/> when given.</summary>
    void Delete(string id, string? expectedVersion = null);

    void Delete(object entity);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    string? GetId(object entity);

    /// <summary>The version of a tracked entity (RavenDB: its change vector). Throws for an entity the session does not track.</summary>
    string? GetVersion(object entity);

    /// <summary>The stored versions of <paramref name="ids"/>, read without loading the documents. Missing ids are absent.</summary>
    Task<IReadOnlyDictionary<string, string>> GetVersionsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);

    /// <summary>The collection <paramref name="type"/> is stored in.</summary>
    string GetCollectionName(Type type);

    /// <summary>The collection a tracked entity was read from or is stored in, per the store's own record of it.</summary>
    string? GetStoredCollectionName(object entity);

    bool IsLoaded(string id);

    bool HasChanged(object entity);

    /// <summary>Every entity the session tracks, new, loaded and deleted.</summary>
    IReadOnlyCollection<SparkTrackedEntity> TrackedEntities { get; }

    void Evict(object entity);

    /// <summary>Forgets every tracked entity and pending operation.</summary>
    void Clear();

    /// <summary>Raised during <see cref="SaveChangesAsync"/> for every entity about to be stored.</summary>
    event Action<object>? BeforeStore;

    /// <summary>Raised during <see cref="SaveChangesAsync"/> for every tracked entity about to be deleted.</summary>
    event Action<object>? BeforeDelete;

    /// <summary>Requests this session has sent so far; 0 for a provider without a request budget.</summary>
    int NumberOfRequests { get; }

    /// <summary>The per-session request budget (RavenDB: MaxNumberOfRequestsPerSession); int.MaxValue when unbounded.</summary>
    int MaxNumberOfRequests { get; set; }

    /// <summary>Executes <paramref name="query"/> (D15 rule 3: core executes only through the provider).</summary>
    Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default);

    Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default);

    /// <summary>The provider's own session object, for provider packages' extensions only.</summary>
    object Native { get; }
}

/// <summary>An entity a session tracks, and whether it is marked for deletion.</summary>
public readonly record struct SparkTrackedEntity(object Entity, bool IsDeleted);

/// <summary>The application's store: opens sessions beside the request-scoped one (S2 F3).</summary>
public interface ISparkStore
{
    /// <summary>The provider's name, for messages ("RavenDB", "InMemory").</summary>
    string ProviderName { get; }

    SparkStoreCapabilities Capabilities { get; }

    /// <summary>A fresh unit of work, independent of the request's. Dispose it.</summary>
    ISparkSession OpenSession();

    /// <summary>The collection <paramref name="type"/> is stored in, per the store's conventions.</summary>
    string GetCollectionName(Type type);

    /// <summary>
    /// Waits, bounded by <paramref name="bound"/>, until reads over <paramref name="collections"/> see the
    /// writes committed before the call. Never throws; a provider whose reads are always current returns at once.
    /// </summary>
    Task WaitForConsistencyAsync(
        ISparkSession session, IReadOnlyCollection<string> collections, TimeSpan bound,
        Action<Exception, string, TimeSpan>? onUnfinished, CancellationToken cancellationToken);
}

/// <summary>
/// A commit found a document's version changed (or the document present/absent) against what the write expected.
/// Providers throw it from <see cref="ISparkSession.SaveChangesAsync"/>, keeping their own exception as the inner one
/// (it carries versions: log it, never send it).
/// </summary>
public sealed class SparkStoreConcurrencyException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What a provider offers beyond the core contract (D13). Feature libraries declare what they need.</summary>
[Flags]
public enum SparkStoreCapabilities
{
    None = 0,
    Subscriptions = 1 << 0,
    Revisions = 1 << 1,
    FullTextSearch = 1 << 2,
    Attachments = 1 << 3,
    Expiration = 1 << 4,
    ChangeNotifications = 1 << 5,
    Indexes = 1 << 6,
}
