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

    /// <summary>
    /// #536 D38: <see cref="Query{T}()"/> for an answer that must include every committed write, such as "is another
    /// Owner left": the query waits up to <paramref name="waitForIndexes"/> for the indexes it reads, including one it
    /// creates (RavenDB's <c>WaitForNonStaleResults</c>). A provider whose reads are always current ignores it.
    /// </summary>
    IQueryable<T> Query<T>(TimeSpan waitForIndexes) where T : class;

    /// <summary>
    /// The rows of the index <typeparamref name="TIndex"/> as <typeparamref name="T"/> (a view root: the generated
    /// <c>VPeople =&gt; Session.Query&lt;VPerson, People_Overview&gt;()</c>). A store without indexes throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    IQueryable<T> Query<T, TIndex>() where T : class where TIndex : class, new();

    /// <summary>
    /// <see cref="Query{T, TIndex}()"/> for an answer that must include every committed write (a reputation total, the
    /// entries a job credits): the query waits up to <paramref name="waitForIndexes"/> for the index, and RavenDB throws
    /// <see cref="TimeoutException"/> when it is still stale then. A provider whose views are always current ignores it.
    /// </summary>
    IQueryable<T> Query<T, TIndex>(TimeSpan waitForIndexes) where T : class where TIndex : class, new();

    Task<T?> LoadAsync<T>(string id, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Loads by id; a missing id maps to null. Keys are the ids asked for.</summary>
    Task<IReadOnlyDictionary<string, T?>> LoadAsync<T>(IEnumerable<string> ids, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Loads by id and asks the store to bring the documents named by <paramref name="includePaths"/> (reference id
    /// properties) along in the same round trip, so later loads of them are served from the session. A hint only: a
    /// provider without includes loads the ids alone, and the references are loaded when read.
    /// </summary>
    Task<IReadOnlyDictionary<string, T?>> LoadAsync<T>(IEnumerable<string> ids, IReadOnlyCollection<string> includePaths, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Brings documents of different types into the session in as few round trips as the store allows (RavenDB: one,
    /// as lazy loads), so the typed loads that follow are served from the session. A hint only: a provider whose loads
    /// are cheap does nothing.
    /// </summary>
    Task PreloadAsync(IReadOnlyCollection<(Type EntityType, string Id)> documents, CancellationToken cancellationToken = default);

    /// <summary>Untyped <see cref="LoadAsync{T}(string, CancellationToken)"/>, for callers that hold a <see cref="Type"/>.</summary>
    Task<object?> LoadAsync(Type entityType, string id, CancellationToken cancellationToken = default);

    /// <summary>Untyped <see cref="LoadAsync{T}(IEnumerable{string}, CancellationToken)"/>.</summary>
    Task<IReadOnlyDictionary<string, object?>> LoadAsync(Type entityType, IEnumerable<string> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// The documents <paramref name="ids"/> name, for a reference to <paramref name="target"/> whose caller checks each
    /// one's stored collection itself (<see cref="GetStoredCollectionName"/>). RavenDB and memory find an id in any
    /// collection, as stored: a document of another type, or one whose CLR type no longer resolves, comes back for the
    /// caller to refuse or accept. A store with a table per type keys its rows per table (#536 D23), so it looks in
    /// <paramref name="target"/>'s table, where every row is a target.
    /// </summary>
    Task<IReadOnlyDictionary<string, object?>> LoadReferencedAsync(Type target, IEnumerable<string> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Documents whose id starts with <paramref name="idPrefix"/>, in id order; with <paramref name="startAfter"/>, only
    /// those whose id sorts after it (keyset paging). The order is the store's own (collation, case-insensitive ids).
    /// </summary>
    Task<IReadOnlyList<T>> LoadStartingWithAsync<T>(string idPrefix, int start = 0, int pageSize = 25, string? startAfter = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Whether a <typeparamref name="T"/> is stored under <paramref name="id"/>. Typed (#536 D27): a relational store
    /// answers from <typeparamref name="T"/>'s table.
    /// </summary>
    Task<bool> ExistsAsync<T>(Id<T> id, CancellationToken cancellationToken = default) where T : class;

    /// <summary><see cref="ExistsAsync{T}"/> for a caller that holds a <see cref="Type"/>.</summary>
    Task<bool> ExistsAsync(Type entityType, string id, CancellationToken cancellationToken = default);

    /// <summary>Tracks <paramref name="entity"/> for the next commit; the provider assigns an id when it has none.</summary>
    Task StoreAsync(object entity, CancellationToken cancellationToken = default);

    Task StoreAsync(object entity, string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tracks <paramref name="entity"/> under <paramref name="id"/>, committed only when the stored version is
    /// still <paramref name="expectedVersion"/> (empty: only when nothing is stored there yet; null: no check).
    /// </summary>
    Task StoreAsync(object entity, string? expectedVersion, string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the <typeparamref name="T"/> stored under <paramref name="id"/> at the next commit, checked against
    /// <paramref name="expectedVersion"/> when given (#536 D27).
    /// </summary>
    void Delete<T>(Id<T> id, string? expectedVersion = null) where T : class;

    /// <summary><see cref="Delete{T}(Id{T}, string?)"/> for a caller that holds a <see cref="Type"/>.</summary>
    void Delete(Type entityType, string id, string? expectedVersion = null);

    void Delete(object entity);

    /// <summary>
    /// When the store may delete <paramref name="entity"/> (#536 S6; RavenDB's <c>@expires</c>), or
    /// <see langword="null"/> for never. The entity must be tracked; <see cref="SaveChangesAsync"/> writes it, and later
    /// writes keep it until it is set again. Deleting is the store's expiration sweep, where it runs: until then the row
    /// stays readable. An <see cref="IExpiring"/> entity needs none of this: its expiry is stamped from the entity.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session does not track <paramref name="entity"/>.</exception>
    void SetExpiry(object entity, DateTimeOffset? expiresAt);

    /// <summary>The expiry of a tracked entity, as stored or as set in this session; <see langword="null"/> for none.</summary>
    DateTimeOffset? GetExpiry(object entity);

    /// <summary>
    /// Every row of <paramref name="query"/>, read as a stream (#536 S6): for scans and backfills over more rows than a
    /// page. The rows are not tracked, so changing one saves nothing and the request budget is not spent per row. Load
    /// a row by its id to change it.
    /// </summary>
    /// <remarks>
    /// RavenDB does not wait for indexing on a stream: a query over an index that may be stale settles it first (a
    /// <c>Take(0)</c> read with the wait). Relational providers page over the key.
    /// </remarks>
    IAsyncEnumerable<SparkStreamResult<T>> StreamAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the unique <paramref name="key"/> (an email, a client id) for the document <paramref name="ownerId"/>
    /// (#536 S6). <see cref="SaveChangesAsync"/> claims it with the documents. The owner claiming it again changes
    /// nothing. A key held by a document that no longer exists is taken over: what a crash or a delete without
    /// <see cref="Release"/> leaves behind. A key another existing document holds fails the save with
    /// <see cref="SparkReservationTakenException"/>, and nothing is written.
    /// </summary>
    /// <remarks>
    /// Relational stores and memory write reservations in the documents' transaction. RavenDB claims them (compare
    /// exchange) before it writes the documents and frees them again when that write fails; the takeover rule covers
    /// a crash in between. Keys are compared case-insensitively; a caller prefixes its own (<c>emails/</c>).
    /// </remarks>
    void Reserve(string key, string ownerId);

    /// <summary>Frees <paramref name="key"/> with the save, but only while <paramref name="ownerId"/> holds it.</summary>
    void Release(string key, string ownerId);

    /// <summary>The document that holds <paramref name="key"/>, read from the store now; <see langword="null"/> when free.</summary>
    Task<string?> GetReservationAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every <typeparamref name="T"/> matching <paramref name="predicate"/> in the store, now and set-based
    /// (#536 S6; RavenDB's delete by query, SQL's <c>DELETE … WHERE</c>), and returns how many.
    /// </summary>
    /// <remarks>
    /// Outside the unit of work: no hooks run (<see cref="BeforeDelete"/>), and copies this session already tracks are
    /// not updated. Where a document's own rules must run, load, delete and save instead.
    /// </remarks>
    Task<long> DeleteWhereAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Appends <paramref name="item"/> to the list at <paramref name="list"/> of the stored document
    /// <paramref name="id"/> (#536 S6). <see cref="SaveChangesAsync"/> applies it to the stored document, not to a
    /// copy this session read, so concurrent appends all land where a load-modify-save would lose one or conflict.
    /// A document that no longer exists is skipped. A copy this session tracks is not updated.
    /// </summary>
    void PatchAppend<T, TItem>(string id, System.Linq.Expressions.Expression<Func<T, IEnumerable<TItem>>> list, TItem item) where T : class;

    /// <summary>
    /// Sets members of every <typeparamref name="T"/> matching <paramref name="predicate"/> in the store, now and
    /// set-based (#536 S6; RavenDB's patch by query, SQL's <c>UPDATE … WHERE</c>), and returns how many. Each updated
    /// row gets a new version.
    /// </summary>
    /// <remarks>The same limits as <see cref="DeleteWhereAsync{T}"/>: no <see cref="BeforeStore"/>, and tracked copies go stale.</remarks>
    Task<long> UpdateWhereAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate, Action<SparkSetters<T>> set, CancellationToken cancellationToken = default) where T : class;

    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// #536 D37: when set, every write checks without an expected version passed: a loaded entity must still be at the
    /// version it was read at, a new one must not exist yet, and <see cref="SaveChangesAsync"/> throws
    /// <see cref="SparkStoreConcurrencyException"/> otherwise (RavenDB: <c>OptimisticConcurrencyMode.Writes</c>).
    /// Off (the default), a new entity whose id is stored replaces it; a loaded one is checked by the relational providers
    /// regardless.
    /// </summary>
    bool OptimisticConcurrency { get; set; }

    string? GetId(object entity);

    /// <summary>The version of a tracked entity (RavenDB: its change vector). Throws for an entity the session does not track.</summary>
    string? GetVersion(object entity);

    /// <summary>
    /// The stored versions of <paramref name="ids"/>, read without loading the documents. Missing ids are absent.
    /// Keys are the canonical ids (#536 D27).
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetVersionsAsync<T>(IEnumerable<Id<T>> ids, CancellationToken cancellationToken = default) where T : class;

    /// <summary><see cref="GetVersionsAsync{T}"/> for a caller that holds a <see cref="Type"/>.</summary>
    Task<IReadOnlyDictionary<string, string>> GetVersionsAsync(Type entityType, IEnumerable<string> ids, CancellationToken cancellationToken = default);

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
    /// <param name="session">The session whose database is meant, or <see langword="null"/> for the store's default.</param>
    /// <returns>Whether the reads caught up within the bound.</returns>
    Task<bool> WaitForConsistencyAsync(
        ISparkSession? session, IReadOnlyCollection<string> collections, TimeSpan bound,
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
