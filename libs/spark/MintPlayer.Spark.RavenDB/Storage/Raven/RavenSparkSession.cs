using System.Collections.Concurrent;
using System.Reflection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using Raven.Client.Documents;
using Raven.Client.Documents.Commands;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.CompareExchange;
using Raven.Client.Documents.Queries;
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

    public IQueryable<T> Query<T>() where T : class => AbsentIsNull(Session.Query<T>());

    public IQueryable<T> Query<T>(TimeSpan waitForIndexes) where T : class
        => AbsentIsNull(Session.Query<T>().Customize(c => c.WaitForNonStaleResults(waitForIndexes)));

    /// <remarks>
    /// A <see cref="Abstractions.Views.SparkView{TRow}"/> is the static index deployed under its name (#536 D32, D42); the
    /// row's members are its stored fields, so the rows read from it are the row type.
    /// </remarks>
    public IQueryable<T> Query<T, TIndex>() where T : class where TIndex : class, new()
        => AbsentIsNull(typeof(Abstractions.Views.ISparkView).IsAssignableFrom(typeof(TIndex))
            ? Session.Query<T>(ViewName<T, TIndex>.Name)
            : IndexQuery<T, TIndex>.Open(Session));

    public IQueryable<T> Query<T, TIndex>(TimeSpan waitForIndexes) where T : class where TIndex : class, new()
        => Query<T, TIndex>() is IRavenQueryable<T> raven ? raven.Customize(c => c.WaitForNonStaleResults(waitForIndexes)) : Query<T, TIndex>();

    private static class ViewName<T, TView> where T : class
    {
        public static readonly string Name = Resolve();

        private static string Resolve()
        {
            var view = Abstractions.Views.SparkViewEntries.Instantiate(typeof(TView));
            if (view.RowType != typeof(T))
                throw new InvalidOperationException($"View {view.Name} yields {view.RowType.Name}, not {typeof(T).Name}.");
            ViewIndexNames.TryAdd(view.Name, 0);
            return view.Name;
        }
    }

    /// <summary>O11: <c>== null</c> also finds a field the document never wrote (<see cref="RavenAbsentNullRewriter"/>).</summary>
    private static IQueryable<T> AbsentIsNull<T>(IQueryable<T> query)
        => query is IRavenQueryable<T> raven ? raven.Customize(c => c.BeforeQueryExecuted(RavenAbsentNullRewriter.Rewrite)) : query;

    /// <summary>
    /// RavenDB's <c>Query&lt;T, TIndex&gt;</c> constrains <typeparamref name="TIndex"/> to its own index base, which the
    /// neutral contract cannot name: bound once per pair, by reflection.
    /// </summary>
    private static class IndexQuery<T, TIndex> where T : class where TIndex : class, new()
    {
        private static readonly Func<IAsyncDocumentSession, IQueryable<T>> open = Bind();

        public static IQueryable<T> Open(IAsyncDocumentSession session) => open(session);

        private static Func<IAsyncDocumentSession, IQueryable<T>> Bind()
        {
            if (!typeof(global::Raven.Client.Documents.Indexes.AbstractCommonApiForIndexes).IsAssignableFrom(typeof(TIndex)))
                throw new ArgumentException($"{typeof(TIndex).FullName} is not a RavenDB index.", nameof(TIndex));
            var method = typeof(IAsyncDocumentSession).GetMethods()
                .Single(m => m.Name == nameof(IAsyncDocumentSession.Query) && m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 0)
                .MakeGenericMethod(typeof(T), typeof(TIndex));
            // A compiled call, not MethodInfo.CreateDelegate: an open-instance delegate over a generic interface method
            // throws NotSupportedException ("Specified method is not supported").
            var session = System.Linq.Expressions.Expression.Parameter(typeof(IAsyncDocumentSession), "session");
            return System.Linq.Expressions.Expression.Lambda<Func<IAsyncDocumentSession, IQueryable<T>>>(
                System.Linq.Expressions.Expression.Call(session, method), session).Compile();
        }
    }

    public Task<T?> LoadAsync<T>(string id, CancellationToken cancellationToken = default) where T : class
        => Session.LoadAsync<T>(id, cancellationToken)!;

    public async Task<IReadOnlyDictionary<string, T?>> LoadAsync<T>(IEnumerable<string> ids, CancellationToken cancellationToken = default) where T : class
        => (await Session.LoadAsync<T>(ids, cancellationToken))!;

    public async Task<IReadOnlyDictionary<string, T?>> LoadAsync<T>(IEnumerable<string> ids, IReadOnlyCollection<string> includePaths, CancellationToken cancellationToken = default) where T : class
    {
        if (includePaths.Count == 0)
            return await LoadAsync<T>(ids, cancellationToken);
        // RavenDB takes includes only on the same fluent call: there is no pre-registering them on the session.
        return (await Session.LoadAsync<T>(ids, builder =>
        {
            foreach (var path in includePaths)
                builder.IncludeDocuments(path);
        }, cancellationToken))!;
    }

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

    /// <remarks>Untyped: an id names one document across every collection, and the caller checks which one.</remarks>
    public Task<IReadOnlyDictionary<string, object?>> LoadReferencedAsync(Type target, IEnumerable<string> ids, CancellationToken cancellationToken = default)
        => LoadAsync<object>(ids, cancellationToken);

    public async Task<IReadOnlyList<T>> LoadStartingWithAsync<T>(string idPrefix, int start = 0, int pageSize = 25, string? startAfter = null, CancellationToken cancellationToken = default) where T : class
        => [.. await Session.Advanced.LoadStartingWithAsync<T>(idPrefix, null, start, pageSize, startAfter: startAfter, token: cancellationToken)];

    public Task<bool> ExistsAsync<T>(Id<T> id, CancellationToken cancellationToken = default) where T : class
        => ExistsAsync(typeof(T), id.Value, cancellationToken);

    /// <remarks>A document id names one document across every collection, so the type isn't needed here.</remarks>
    public Task<bool> ExistsAsync(Type entityType, string id, CancellationToken cancellationToken = default)
        => string.IsNullOrEmpty(id) ? Task.FromResult(false) : Session.Advanced.ExistsAsync(id, cancellationToken);

    public Task StoreAsync(object entity, CancellationToken cancellationToken = default)
        => Session.StoreAsync(entity, cancellationToken);

    public Task StoreAsync(object entity, string id, CancellationToken cancellationToken = default)
        => Session.StoreAsync(entity, id, cancellationToken);

    public Task StoreAsync(object entity, string? expectedVersion, string id, CancellationToken cancellationToken = default)
        => Session.StoreAsync(entity, expectedVersion, id, cancellationToken);

    public void Delete<T>(Id<T> id, string? expectedVersion = null) where T : class
        => Delete(typeof(T), id.Value, expectedVersion);

    public void Delete(Type entityType, string id, string? expectedVersion = null)
    {
        if (expectedVersion is null)
            Session.Delete(id);
        else
            Session.Delete(id, expectedVersion);
    }

    public void Delete(object entity)
    {
        // A string here is an id meant for Delete(Type, id): the untyped Delete(string) is gone (#536 D27).
        if (entity is string)
            throw new ArgumentException("Delete(object) takes an entity; delete by id with Delete<T>(Id<T>) or Delete(Type, string).", nameof(entity));
        Session.Delete(entity);
    }

    /// <remarks>
    /// Reservations are compare-exchange values, which a session that is not cluster-wide cannot write with its
    /// documents. So the claims come first, marked pending (<see cref="PendingSince"/>), the documents second, and a
    /// failed write frees the claims it made; after a successful write the claims lose their mark and the releases
    /// follow. A claim still pending may be a write in flight, so nobody takes it over for <see cref="PendingGrace"/>.
    /// A crash in between leaves a claim whose owner does not exist, which a claimant takes over after that.
    /// </remarks>
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var claimed = new List<(string Key, string Owner, long Index)>();
        try
        {
            foreach (var (key, owner, _) in reservations.Where(r => r.Claim))
                if (await ClaimAsync(key, owner, cancellationToken) is { } index)
                    claimed.Add((key, owner, index));

            await Session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            foreach (var (key, _, index) in claimed)
                await Operations.SendAsync(new DeleteCompareExchangeValueOperation<string>(key, index), token: CancellationToken.None);
            if (ex is global::Raven.Client.Exceptions.ConcurrencyException)
                throw new SparkStoreConcurrencyException("A document changed since the write expected it.", ex);
            throw;
        }

        // The owners are written: their claims are no longer in flight. Without a mark, a claim whose owner is deleted
        // later is taken over at once.
        foreach (var (key, owner, index) in claimed)
            await Operations.SendAsync(new PutCompareExchangeValueOperation<string>(key, owner, index), token: CancellationToken.None);

        foreach (var (key, owner, _) in reservations.Where(r => !r.Claim))
        {
            var current = await Operations.SendAsync(new GetCompareExchangeValueOperation<string>(key), token: cancellationToken);
            if (current is not null && string.Equals(current.Value, owner, StringComparison.OrdinalIgnoreCase))
                await Operations.SendAsync(new DeleteCompareExchangeValueOperation<string>(key, current.Index), token: cancellationToken);
        }
        reservations.Clear();
    }

    /// <remarks>RavenDB's typed patch: the session sends it in the save's batch, and the server appends to the stored array.</remarks>
    public void PatchAppend<T, TItem>(string id, System.Linq.Expressions.Expression<Func<T, IEnumerable<TItem>>> list, TItem item) where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        Session.Advanced.Patch<T, TItem>(id, list, array => array.Add(item));
    }

    // ---- set-based ----

    /// <summary>How long a set-based operation waits for a stale index, and for itself to finish.</summary>
    private static readonly TimeSpan BulkTimeout = TimeSpan.FromMinutes(5);

    public async Task<long> DeleteWhereAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) where T : class
    {
        var operation = await Operations.SendAsync(new DeleteByQueryOperation(IndexQueryFor(predicate), BulkOptions), token: cancellationToken);
        return (await operation.WaitForCompletionAsync<BulkOperationResult>(BulkTimeout)).Total;
    }

    /// <remarks>
    /// The LINQ query renders the RQL and its parameters; the assignments are appended as <c>update { … }</c>. Field paths
    /// are validated identifiers (<see cref="RqlIdentifier.FieldPath"/>) and values are parameters: nothing is spliced.
    /// </remarks>
    public async Task<long> UpdateWhereAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate, Action<SparkSetters<T>> set, CancellationToken cancellationToken = default) where T : class
    {
        var setters = new SparkSetters<T>();
        set(setters);
        if (setters.Setters.Count == 0)
            throw new ArgumentException("UpdateWhere needs at least one Set.", nameof(set));

        var query = IndexQueryFor(predicate);
        var parameters = query.QueryParameters ?? new global::Raven.Client.Parameters();
        var assignments = new System.Text.StringBuilder();
        for (var i = 0; i < setters.Setters.Count; i++)
        {
            var name = $"__spark_set{i}";
            assignments.Append($"this.{RqlIdentifier.FieldPath(setters.Setters[i].Path)} = ${name}; ");
            parameters[name] = setters.Setters[i].Value;
        }
        query.Query += $" update {{ {assignments}}}";
        query.QueryParameters = parameters;

        var operation = await Operations.SendAsync(new PatchByQueryOperation(query, BulkOptions), token: cancellationToken);
        return (await operation.WaitForCompletionAsync<BulkOperationResult>(BulkTimeout)).Total;
    }

    private static QueryOperationOptions BulkOptions => new() { AllowStale = false, StaleTimeout = BulkTimeout, RetrieveDetails = false };

    private IndexQuery IndexQueryFor<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate) where T : class
    {
        var query = ((IRavenQueryInspector)RavenIdQueryRewriter.Rewrite(Session.Query<T>().Where(predicate))).GetIndexQuery(isAsync: true);
        RavenAbsentNullRewriter.Rewrite(query);
        return query;
    }

    // ---- reservations ----

    private readonly List<(string Key, string Owner, bool Claim)> reservations = [];

    private global::Raven.Client.Documents.Operations.OperationExecutor Operations
        => Store.Operations.ForDatabase(((InMemoryDocumentSessionOperations)Session).DatabaseName);

    public void Reserve(string key, string ownerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerId);
        reservations.Add((key, ownerId, true));
    }

    public void Release(string key, string ownerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerId);
        reservations.Add((key, ownerId, false));
    }

    public async Task<string?> GetReservationAsync(string key, CancellationToken cancellationToken = default)
    {
        var current = await Operations.SendAsync(new GetCompareExchangeValueOperation<string>(key), token: cancellationToken);
        return string.IsNullOrEmpty(current?.Value) ? null : current.Value;
    }

    /// <summary>
    /// The metadata of a claim whose documents are not written yet: when it was claimed (UTC, round-trip format).
    /// </summary>
    internal const string PendingSince = "Spark-Pending-Since";

    /// <summary>
    /// How long a pending claim is never taken over. A write lasts seconds; past this, a pending claim whose owner does
    /// not exist is what a crash between the claim and the write left behind. The margin also covers the clocks of
    /// different hosts.
    /// </summary>
    internal static readonly TimeSpan PendingGrace = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Claims <paramref name="key"/> for <paramref name="owner"/>, marked pending: the new index when this call wrote it,
    /// or <see langword="null"/> when the owner already held it. Takes over a key whose holder no longer exists and is
    /// not a pending claim within <see cref="PendingGrace"/>, against the index just read, so two claimants cannot both
    /// win. An empty value (a claim in flight under the pre-#536 protocol) is never taken over.
    /// </summary>
    private async Task<long?> ClaimAsync(string key, string owner, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = await Operations.SendAsync(new GetCompareExchangeValueOperation<string>(key), token: cancellationToken);
            if (existing is null)
            {
                var created = await Operations.SendAsync(new PutCompareExchangeValueOperation<string>(key, owner, 0, Pending()), token: cancellationToken);
                if (created.Successful)
                    return created.Index;
                continue;
            }

            if (string.Equals(existing.Value, owner, StringComparison.OrdinalIgnoreCase))
                return null;
            if (string.IsNullOrEmpty(existing.Value) || InFlight(existing) || await OwnerExistsAsync(existing.Value, cancellationToken))
                throw new SparkReservationTakenException(key, existing.Value ?? string.Empty);

            var takenOver = await Operations.SendAsync(new PutCompareExchangeValueOperation<string>(key, owner, existing.Index, Pending()), token: cancellationToken);
            if (takenOver.Successful)
                return takenOver.Index;
        }

        var holder = await Operations.SendAsync(new GetCompareExchangeValueOperation<string>(key), token: cancellationToken);
        throw new SparkReservationTakenException(key, holder?.Value ?? string.Empty);
    }

    private static global::Raven.Client.Json.MetadataAsDictionary Pending()
        => new() { [PendingSince] = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) };

    /// <summary>A claim marked pending less than <see cref="PendingGrace"/> ago: its owner may be being written right now.</summary>
    private static bool InFlight(CompareExchangeValue<string> claim)
        => claim.Metadata is { } metadata
            && metadata.TryGetValue(PendingSince, out object? since)
            && DateTime.TryParse(since?.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
            && DateTime.UtcNow - at.ToUniversalTime() < PendingGrace;

    /// <summary>Asked of a session of its own: this one's request budget and identity map stay untouched.</summary>
    private async Task<bool> OwnerExistsAsync(string id, CancellationToken cancellationToken)
    {
        using var probe = Store.OpenAsyncSession(((InMemoryDocumentSessionOperations)Session).DatabaseName);
        return await probe.Advanced.ExistsAsync(id, cancellationToken);
    }

    /// <remarks>The <c>@expires</c> metadata, in UTC. RavenDB keeps metadata across writes, and a metadata change alone is a write.</remarks>
    public void SetExpiry(object entity, DateTimeOffset? expiresAt)
    {
        var metadata = TrackedMetadata(entity);
        if (expiresAt is { } value)
            metadata[global::Raven.Client.Constants.Documents.Metadata.Expires] = value.UtcDateTime.ToString("O");
        else
            metadata.Remove(global::Raven.Client.Constants.Documents.Metadata.Expires);
    }

    public DateTimeOffset? GetExpiry(object entity)
        => TrackedMetadata(entity).TryGetValue(global::Raven.Client.Constants.Documents.Metadata.Expires, out object? value)
            ? value switch
            {
                null => null,
                DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
                DateTimeOffset offset => offset.ToUniversalTime(),
                _ => DateTimeOffset.Parse(value.ToString()!, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal),
            }
            : null;

    public async IAsyncEnumerable<SparkStreamResult<T>> StreamAsync<T>(IQueryable<T> query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var stream = await Session.Advanced.StreamAsync(RavenIdQueryRewriter.Rewrite(ViewRows(query)), cancellationToken);
        while (await stream.MoveNextAsync())
            yield return new SparkStreamResult<T>(stream.Current.Id, stream.Current.Document, stream.Current.ChangeVector);
    }

    private IMetadataDictionary TrackedMetadata(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (Session.Advanced.GetDocumentId(entity) is null)
            throw new InvalidOperationException($"{entity.GetType().Name} is not associated with the session; store or load it first.");
        return Session.Advanced.GetMetadataFor(entity);
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
    public Task<IReadOnlyDictionary<string, string>> GetVersionsAsync<T>(IEnumerable<Id<T>> ids, CancellationToken cancellationToken = default) where T : class
        => GetVersionsAsync(typeof(T), ids.Select(id => id.Value), cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> GetVersionsAsync(Type entityType, IEnumerable<string> ids, CancellationToken cancellationToken = default)
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

    public void Clear()
    {
        Session.Advanced.Clear();
        reservations.Clear();
    }

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

    public bool OptimisticConcurrency
    {
        get => Session.Advanced.OptimisticConcurrencyMode != global::Raven.Client.Documents.Session.OptimisticConcurrencyMode.None;
        set
        {
            if (value != OptimisticConcurrency) // keeps WritesAndReads, set on the raw session, when asked for on
                Session.Advanced.OptimisticConcurrencyMode = value
                    ? global::Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes
                    : global::Raven.Client.Documents.Session.OptimisticConcurrencyMode.None;
        }
    }

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
        => query is IRavenQueryable<T> && RavenIdQueryRewriter.Rewrite(ViewRows(query)) is IRavenQueryable<T> raven
            ? await raven.ToListAsync(cancellationToken)
            : [.. query];

    /// <summary>Index names of the <see cref="Abstractions.Views.SparkView{TRow}"/>s this process has queried.</summary>
    private static readonly ConcurrentDictionary<string, byte> ViewIndexNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The rows of a view, read as the view computed them: <c>ProjectInto&lt;T&gt;()</c> when the caller made no projection
    /// of its own. Without it RavenDB answers a map view's query with the source documents, so a computed column comes
    /// back as the document's field of that name or empty (measured: <c>Label</c> empty, <c>Points</c> the document's 2
    /// instead of the view's 20). It is added here, at execution, and not when the query is opened: the caller still
    /// composes <c>Where</c>/<c>OrderBy</c>/<c>Take</c> on the row type, and RavenDB refuses a second projection (a
    /// <c>Select(r =&gt; r.Id)</c> after it).
    /// </summary>
    private static IQueryable<T> ViewRows<T>(IQueryable<T> query)
    {
        if (query is not IRavenQueryInspector { IndexName: { } index } || !ViewIndexNames.ContainsKey(index))
            return query;
        for (var expression = query.Expression; expression is System.Linq.Expressions.MethodCallExpression call; expression = call.Arguments[0])
        {
            if (call.Method.Name is "Select" or "ProjectInto" or "OfType" or "As")
                return query;
        }
        return query.ProjectInto<T>();
    }

    public async Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
        => query is IRavenQueryable<T> && RavenIdQueryRewriter.Rewrite(query) is IRavenQueryable<T> raven
            ? await raven.CountAsync(cancellationToken)
            : query.Count();

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

    /// <remarks>
    /// A typed lazy load per document, executed together: one round trip. Typed, so the later typed read of the same
    /// id finds an entity of its own type.
    /// </remarks>
    public async Task PreloadAsync(IReadOnlyCollection<(Type EntityType, string Id)> documents, CancellationToken cancellationToken = default)
    {
        if (documents.Count == 0)
            return;
        var lazily = Session.Advanced.Lazily;
        foreach (var (entityType, id) in documents)
            LazyLoad.GetOrAdd(entityType, static t => LazyLoadOpen.MakeGenericMethod(t)).Invoke(lazily, [id, cancellationToken]);
        await Session.Advanced.Eagerly.ExecuteAllPendingLazyOperationsAsync(cancellationToken);
    }

    private static readonly MethodInfo LazyLoadOpen = typeof(global::Raven.Client.Documents.Session.Operations.Lazy.IAsyncLazySessionOperations)
        .GetMethods()
        .Single(m => m.Name == nameof(global::Raven.Client.Documents.Session.Operations.Lazy.IAsyncLazySessionOperations.LoadAsync)
            && m.IsGenericMethodDefinition
            && m.GetParameters() is [{ ParameterType: var id }, { ParameterType: var token }]
            && id == typeof(string) && token == typeof(CancellationToken));

    private static readonly ConcurrentDictionary<Type, MethodInfo> LazyLoad = new();
}
