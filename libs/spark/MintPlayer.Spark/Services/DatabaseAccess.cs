using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Exceptions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using System.Reflection;

namespace MintPlayer.Spark.Services;

[Register(typeof(IDatabaseAccess), ServiceLifetime.Scoped)]
internal partial class DatabaseAccess : IDatabaseAccess
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IActionsResolver actionsResolver;
    [Inject] private readonly IServiceProvider serviceProvider;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IReferenceResolver referenceResolver;
    [Inject] private readonly Breadcrumb.IBreadcrumbResolver breadcrumbResolver;
    [Inject] private readonly IRowSecurity rowSecurity;
    [Inject] private readonly ICollectionGuard collectionGuard;
    [Inject] private readonly ISparkTypeResolver typeResolver;

    public async Task<T?> GetDocumentUncheckedAsync<T>(string id) where T : class
    {
        return await session.LoadAsync<T>(id);
    }

    public async Task<IEnumerable<T>> GetDocumentsUncheckedAsync<T>() where T : class
    {
        return await session.Query<T>().ToListAsync();
    }

    public async Task<IEnumerable<T>> GetDocumentsByObjectTypeIdUncheckedAsync<T>(Guid objectTypeId) where T : class
    {
        return await session.Query<T>()
            .Where(x => ((PersistentObject)(object)x).ObjectTypeId == objectTypeId)
            .ToListAsync();
    }

    public async Task<T> SaveDocumentUncheckedAsync<T>(T document) where T : class
    {
        await session.StoreAsync(document);
        await session.SaveChangesAsync();

        // If this is a replicated entity, also broadcast the changes to the owner module
        var interceptor = serviceProvider.GetService<ISyncActionInterceptor>();
        if (interceptor != null && interceptor.IsReplicated(typeof(T)))
        {
            var idProperty = typeof(T).GetCachedProperty("Id");
            var documentId = idProperty is not null && idProperty.CanRead
                ? AccessorCache.GetGetter(idProperty)(document)?.ToString()
                : null;
            await interceptor.HandleSaveAsync(document, documentId);
        }

        return document;
    }

    public async Task DeleteDocumentUncheckedAsync<T>(string id) where T : class
    {
        session.Delete(id);
        await session.SaveChangesAsync();

        // If this is a replicated entity, also notify the owner module
        var interceptor = serviceProvider.GetService<ISyncActionInterceptor>();
        if (interceptor != null && interceptor.IsReplicated(typeof(T)))
        {
            await interceptor.HandleDeleteAsync(typeof(T), id);
        }
    }

    // PersistentObject-specific methods that handle entity mapping

    public async Task<PersistentObject?> GetPersistentObjectAsync(Guid objectTypeId, string id)
    {
        var entityTypeDefinition = modelLoader.GetEntityType(objectTypeId);
        if (entityTypeDefinition == null) return null;

        await permissionService.EnsureAuthorizedAsync("Read", entityTypeDefinition.Name);

        var clrType = entityTypeDefinition.ClrType;
        var entityType = typeResolver.Resolve(clrType);
        if (entityType == null) return null;

        // Composition seam (#324): an Actions class overriding OnComposeAsync serves this type's
        // page from code instead of the database — the start-page pattern. Runs under the
        // type-level "Read" check above and short-circuits everything below it. Skipping the
        // collection guard and row security here is sound, not a hole: both exist to police
        // *database documents* (id-to-type confusion, per-row visibility of stored data), and a
        // composed object corresponds to no document — the Actions class hand-picks every value
        // it exposes, and it is the same authority those guards defer to. The composed object is
        // forced read-only (Can = none) unless the hook says otherwise; anything interactive on
        // such a page is a custom action with its own authorization.
        var composed = await ComposeViaActionsAsync(entityType, objectTypeId, id);
        if (composed is not null)
            return composed;

        // Use actions for loading
        var entity = await LoadEntityViaActionsAsync(session, entityType, id);

        if (entity == null) return null;

        // Id-to-type binding (security sweep C1): the caller authorized `entityType`, but `id` is
        // untrusted and RavenDB's LoadAsync deserializes a foreign-collection document into it all
        // the same. A document whose real @collection isn't this type's is not this type's — 404,
        // indistinguishable from missing (M-3), so it's no oracle for cross-collection existence.
        if (!collectionGuard.BelongsToAuthorizedCollection(session, entity, entityType))
            return null;

        // Row-level read gate. Entity-type "Read" passed; now let the Actions class decide
        // whether this specific instance is visible to the current caller. Returning null
        // here propagates as 404 through the endpoint — same shape as a genuinely missing
        // record, per M-3 (authorized-but-forbidden must be indistinguishable from not-found).
        if (!await rowSecurity.IsAllowedAsync(entityType, "Read", entity))
            return null;

        // Resolve breadcrumbs (recursive, batched) for this entity and its references.
        var breadcrumbs = await breadcrumbResolver.ResolveAsync(session, [entity], entityTypeDefinition);

        var persistentObject = entityMapper.ToPersistentObject(entity, objectTypeId, breadcrumbs);
        // Per-viewer attribute redaction (#236 G4) — the Actions class may hide specific
        // attributes of this row from this caller (e.g. a secret only managers may view).
        await rowSecurity.RedactAsync(session, [(persistentObject, entity)], entityType, entityType, "Read");
        // Per-row affordances (#236 G5): when the type has a row rule, tell the client whether it
        // may edit/delete THIS row, so the generic UI doesn't render buttons that would 404. One
        // row, negligible cost; absent for types with no row rule (clients fall back to type-level).
        // Invariant (#243): the block never claims more than GET /spark/permissions/{type} — the
        // row rule can only narrow the type-level right, never widen it. Type-level first: it's a
        // cheap in-memory check, and when it already denies, the consumer's row hook isn't invoked.
        if (rowSecurity.HasRowRule(entityType))
        {
            persistentObject.Can = new PersistentObjectPermissions
            {
                Edit = await permissionService.IsAllowedAsync("Edit", entityTypeDefinition.Name)
                    && await rowSecurity.IsAllowedAsync(entityType, "Edit", entity),
                Delete = await permissionService.IsAllowedAsync("Delete", entityTypeDefinition.Name)
                    && await rowSecurity.IsAllowedAsync(entityType, "Delete", entity),
            };
        }
        // Capture the RavenDB change vector so clients can round-trip it for optimistic concurrency.
        persistentObject.Etag = session.Advanced.GetChangeVectorFor(entity);
        return persistentObject;
    }

    public async Task<IEnumerable<PersistentObject>> GetPersistentObjectsAsync(Guid objectTypeId)
    {
        var entityTypeDefinition = modelLoader.GetEntityType(objectTypeId);
        if (entityTypeDefinition == null) return [];

        await permissionService.EnsureAuthorizedAsync("Query", entityTypeDefinition.Name);

        var clrType = entityTypeDefinition.ClrType;
        var entityType = typeResolver.Resolve(clrType);
        if (entityType == null) return [];

        // Declared binding (issue #279): the entity file's queryType/indexName — written by the
        // synchronizer and hash-covered — replaces the ambient registry lookup. An empty binding
        // queries the raw collection; a binding whose projection type no longer resolves is a loud
        // error, because the silent alternative is a grid of null computed fields.
        Type queryType = entityType;
        string? indexName = null;

        if (!string.IsNullOrEmpty(entityTypeDefinition.IndexName) && !string.IsNullOrEmpty(entityTypeDefinition.QueryType))
        {
            queryType = typeResolver.Resolve(entityTypeDefinition.QueryType)
                ?? throw new InvalidOperationException(
                    $"Entity '{entityTypeDefinition.Name}' declares projection '{entityTypeDefinition.QueryType}' " +
                    $"(index '{entityTypeDefinition.IndexName}'), but the type does not resolve. Re-run " +
                    $"--spark-synchronize-model, or register the assembly declaring it via AddIndexesFrom(...).");
            indexName = entityTypeDefinition.IndexName;
        }

        // Include paths — [Reference] property names + GetDefaultIncludes() (#239), deduped.
        var includePaths = referenceResolver.ResolveIncludePaths(queryType, entityType);

        // Query entities - use index if projection is registered, otherwise query collection
        var entities = (await QueryEntitiesWithIncludesAsync(session, entityType, queryType, indexName, includePaths)).ToList();

        // Row-level "Query" gate (H-2): after entity-type authz passed, filter the list down
        // to rows the Actions class says the caller may see. For projection queries, the row
        // filter takes the base entity (CarActions typed on Car, not VCar) so we load the
        // matching base docs through the session cache. This filters after materialization, so a
        // row-scoped type reads its whole collection per query; pushing the predicate into RavenDB
        // is a known follow-up.
        entities = (await rowSecurity.FilterAsync(session, entities, entityType, queryType, "Query")).ToList();

        // Resolve breadcrumbs for the page. The .Include() from QueryEntitiesWithIncludesAsync
        // primed level-1 references into the session cache, so the resolver's first batched
        // load is a cache hit; deeper levels cost one batched request each.
        var breadcrumbs = await breadcrumbResolver.ResolveAsync(session, entities, entityTypeDefinition);

        var mapped = entities
            .Select(e => (Po: entityMapper.ToPersistentObject(e, objectTypeId, breadcrumbs), Row: e))
            .ToList();
        await rowSecurity.RedactAsync(session, mapped, entityType, queryType, "Query");
        return mapped.Select(m => m.Po);
    }

    /// <summary>
    /// Applies the Actions class's row-level read gate to a materialized list. When the
    /// query ran against a projection type, we load the corresponding base entities from
    /// the session (Raven reuses its cache, so this is cheap for documents already seen)
    /// and evaluate the filter against those — the Actions class is typed on the base
    /// entity, not the projection.
    /// </summary>
    /// <summary>
    /// Answers "may this caller save this object?" without saving it.
    /// <para>
    /// Exists so an endpoint can ask <b>before</b> spending work on the request — specifically
    /// before validating it. Validation used to run first (N23), so a caller with no right to
    /// create an entity type received a 400 listing that type's validation errors and only reached
    /// 401/403 when the payload happened to be well-formed. The refusal was never in doubt; what
    /// leaked was which attributes a type requires, to someone who cannot create one.
    /// </para>
    /// <para>
    /// This is not a second copy of the rule. <see cref="SavePersistentObjectAsync"/> calls this
    /// same method, so there is one implementation of the decision and the chokepoint remains
    /// authoritative — the endpoint merely asks it earlier.
    /// </para>
    /// </summary>
    public async Task EnsureSaveAuthorizedAsync(PersistentObject persistentObject)
    {
        var entityTypeDefinition = modelLoader.GetEntityType(persistentObject.ObjectTypeId)
            ?? throw new InvalidOperationException($"Could not find EntityType with ID '{persistentObject.ObjectTypeId}'");

        // Id decides the verb: absent means this is a creation, present means an edit.
        var action = string.IsNullOrEmpty(persistentObject.Id) ? "New" : "Edit";
        await permissionService.EnsureAuthorizedAsync(action, entityTypeDefinition.Name);
    }

    public async Task<PersistentObject> SavePersistentObjectAsync(PersistentObject persistentObject)
    {
        var entityTypeDefinition = modelLoader.GetEntityType(persistentObject.ObjectTypeId)
            ?? throw new InvalidOperationException($"Could not find EntityType with ID '{persistentObject.ObjectTypeId}'");

        var entityType = typeResolver.Resolve(entityTypeDefinition.ClrType)
            ?? throw new InvalidOperationException($"Could not resolve type '{entityTypeDefinition.ClrType}'");

        // Natural-id create-collision (security sweep H2): for an IHasNaturalId type the document
        // id is derived from the entity's own contents, so a "create" (Id == null) whose derived id
        // already exists is really an overwrite — and the New branch skips the Edit right, the row
        // Edit gate, the collection guard, and the concurrency check. Detect the collision and set
        // the id, so the request flows through the Edit path below (and EnsureSaveAuthorizedAsync
        // then checks "Edit", not "New"). A caller with only New rights can no longer rewrite an
        // existing document by replaying its natural key.
        if (string.IsNullOrEmpty(persistentObject.Id)
            && typeof(IHasNaturalId).IsAssignableFrom(entityType))
        {
            var probe = entityMapper.ToEntity(persistentObject) as IHasNaturalId;
            var derivedId = probe?.GetId();
            if (!string.IsNullOrEmpty(derivedId))
            {
                using var probeSession = documentStore.OpenAsyncSession();
                if (await probeSession.Advanced.ExistsAsync(derivedId))
                    persistentObject.Id = derivedId;
            }
        }

        await EnsureSaveAuthorizedAsync(persistentObject);

        // Row-level Edit gate (R2-H2): for an update against an existing entity, the
        // Actions class's IsAllowedAsync(Edit, entity) hook decides whether THIS caller
        // can edit THIS instance. Round 1's H-2 fix only covered the Read/Query paths;
        // writes silently inherited "if you can read it, you can overwrite it" — Alice
        // could overwrite Bob's records if she could read them. We load the existing
        // entity through a side session (same session as the etag check) so the row
        // gate sees the pre-update state. New entities (Id == null) skip the gate —
        // there's no instance yet to filter on; the entity-type-level "New" check
        // above is sufficient.
        if (!string.IsNullOrEmpty(persistentObject.Id))
        {
            using var checkSession = documentStore.OpenAsyncSession();
            var existing = await LoadEntityAsync(checkSession, entityType, persistentObject.Id);
            if (existing is not null)
            {
                // Id-to-type binding (security sweep C1/H1): the update targets an existing
                // document by a client-supplied id. If that document isn't actually of the
                // authorized type's collection, the caller is trying to overwrite a foreign
                // document (a Customer edit rewriting a SparkUser). Treat as not-found — the
                // update endpoint maps SparkRowLevelAccessDeniedException to 404. Covers the sync
                // path too: SyncActionHandler routes module writes through here.
                if (!collectionGuard.BelongsToAuthorizedCollection(checkSession, existing, entityType))
                    throw new SparkRowLevelAccessDeniedException($"Edit/{entityTypeDefinition.Name}");

                // Concurrency check folds into the same side session — see R2-M7 / M-7.
                if (!string.IsNullOrEmpty(persistentObject.Etag))
                {
                    var currentEtag = checkSession.Advanced.GetChangeVectorFor(existing);
                    if (!string.Equals(currentEtag, persistentObject.Etag, StringComparison.Ordinal))
                        throw new SparkConcurrencyException(persistentObject.Etag, currentEtag);
                }

                if (!await rowSecurity.IsAllowedAsync(entityType, "Edit", existing))
                    throw new SparkRowLevelAccessDeniedException($"Edit/{entityTypeDefinition.Name}");
            }
        }

        // Pass PO directly to actions — entity mapping happens inside the actions pipeline
        var savedEntity = await SaveEntityViaActionsAsync(session, entityType, persistentObject);

        // Get the generated ID from the entity
        var idProperty = entityType.GetCachedProperty("Id");
        var generatedId = idProperty is not null && idProperty.CanRead
            ? AccessorCache.GetGetter(idProperty)(savedEntity)?.ToString()
            : null;

        persistentObject.Id = generatedId;
        // Return the fresh change vector so the client can round-trip it to the next update.
        persistentObject.Etag = session.Advanced.GetChangeVectorFor(savedEntity);

        // If this is a replicated entity, also broadcast the changes to the owner module
        var interceptor = serviceProvider.GetService<ISyncActionInterceptor>();
        if (interceptor != null && interceptor.IsReplicated(entityType))
        {
            await interceptor.HandleSaveAsync(entityType, persistentObject);
        }

        return persistentObject;
    }

    public async Task DeletePersistentObjectAsync(Guid objectTypeId, string id)
    {
        var entityTypeDefinition = modelLoader.GetEntityType(objectTypeId);
        if (entityTypeDefinition == null) return;

        await permissionService.EnsureAuthorizedAsync("Delete", entityTypeDefinition.Name);

        var clrType = entityTypeDefinition.ClrType;
        var entityType = typeResolver.Resolve(clrType);
        if (entityType == null) return;

        // Row-level Delete gate (R2-H2): same shape as the Edit gate in
        // SavePersistentObjectAsync — load the entity in a side session and ask
        // the Actions class. Apps can permit Read-everyone but Delete-owner-only.
        var existing = await LoadEntityAsync(session, entityType, id);
        if (existing is null) return; // Nothing to delete; preserves 404-on-missing semantics.
        // Id-to-type binding (security sweep C1/H1): don't let a Delete on one type erase a
        // document of another by naming its id. A foreign-collection document is "not found" here.
        if (!collectionGuard.BelongsToAuthorizedCollection(session, existing, entityType))
            return;
        if (!await rowSecurity.IsAllowedAsync(entityType, "Delete", existing))
            throw new SparkRowLevelAccessDeniedException($"Delete/{entityTypeDefinition.Name}");

        // Delete locally first (includes before hook)
        await DeleteEntityViaActionsAsync(session, entityType, id);

        // If this is a replicated entity, also notify the owner module
        var interceptor = serviceProvider.GetService<ISyncActionInterceptor>();
        if (interceptor != null && interceptor.IsReplicated(entityType))
        {
            await interceptor.HandleDeleteAsync(entityType, id);
        }
    }

    private async Task<object?> LoadEntityAsync(IAsyncDocumentSession session, Type entityType, string id)
    {
        var genericMethod = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo?>(
            ("DatabaseAccess.SessionLoadAsync", entityType),
            static k =>
            {
                var method = typeof(IAsyncDocumentSession).GetMethod(
                    nameof(IAsyncDocumentSession.LoadAsync),
                    [typeof(string), typeof(CancellationToken)]);
                return method?.MakeGenericMethod(k.Type);
            });
        var task = genericMethod?.Invoke(session, [id, CancellationToken.None]) as Task;

        if (task == null) return null;

        await task;

        return task.GetCompletedTaskResult();
    }

    /// <summary>
    /// Queries entities and uses .Include() for all reference properties so that
    /// referenced documents are loaded in a single database call.
    /// When indexName is provided, queries the RavenDB index instead of the collection.
    /// </summary>
    private async Task<IEnumerable<object>> QueryEntitiesWithIncludesAsync(
        IAsyncDocumentSession session,
        Type baseEntityType,
        Type entityType,
        string? indexName,
        IReadOnlyCollection<string> includePaths)
    {
        object? query;

        // Query method signature: Query<T>(string indexName, string collectionName, bool isMapReduce)
        var sessionType = session.GetType();
        var genericQueryMethod = ReflectionCache.GetOrAdd<(string Op, Type Session, Type Entity), MethodInfo?>(
            ("DatabaseAccess.SessionQuery3", sessionType, entityType),
            static k =>
            {
                var queryMethod = k.Session.GetMethods()
                    .FirstOrDefault(m => m.Name == "Query"
                        && m.GetGenericArguments().Length == 1
                        && m.GetParameters().Length == 3);
                return queryMethod?.MakeGenericMethod(k.Entity);
            });

        if (genericQueryMethod == null)
            return [];

        // Pass indexName if querying an index, null for collection query
        // RavenDB converts underscores to slashes in index names (e.g., "People_Overview" -> "People/Overview")
        var ravenIndexName = indexName?.Replace("_", "/");
        query = genericQueryMethod.Invoke(session, [ravenIndexName, null, false]);

        if (query == null)
            return [];

        // When querying an index, use ProjectInto<T>() to project from stored fields
        // This ensures computed/stored fields like FullName are populated from the index
        if (!string.IsNullOrEmpty(indexName))
        {
            var genericProjectIntoMethod = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo?>(
                ("DatabaseAccess.LinqProjectInto", entityType),
                static k =>
                {
                    var projectIntoMethod = typeof(LinqExtensions).GetMethods()
                        .FirstOrDefault(m => m.Name == "ProjectInto"
                            && m.IsGenericMethod
                            && m.GetGenericArguments().Length == 1
                            && m.GetParameters().Length == 1
                            && m.GetParameters()[0].ParameterType == typeof(IQueryable));
                    return projectIntoMethod?.MakeGenericMethod(k.Type);
                });

            if (genericProjectIntoMethod != null)
            {
                query = genericProjectIntoMethod.Invoke(null, [query])!;
            }
        }

        // Chain .Include(path) so referenced documents are loaded in the same round-trip
        if (query != null && includePaths.Count > 0)
        {
            query = referenceResolver.ApplyIncludes(query, entityType, includePaths);
        }

        // Push the row filter into the query where shapes allow (no projection in play);
        // otherwise FilterAsync in the caller stays the gate.
        if (query != null)
        {
            query = await rowSecurity.ComposeRowFilterAsync(query, baseEntityType, entityType, "Query");
        }

        // Call ToListAsync on the query
        var genericToListMethod = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo?>(
            ("DatabaseAccess.LinqToListAsync", entityType),
            static k =>
            {
                var toListMethod = typeof(LinqExtensions).GetMethods()
                    .FirstOrDefault(m => m.Name == nameof(LinqExtensions.ToListAsync)
                        && m.GetGenericArguments().Length == 1
                        && m.GetParameters().Length == 2);
                return toListMethod?.MakeGenericMethod(k.Type);
            });

        if (genericToListMethod == null)
            return [];

        var task = genericToListMethod.Invoke(null, [query, CancellationToken.None]) as Task;

        if (task == null)
            return [];

        await task;

        var result = task.GetCompletedTaskResult();

        if (result is System.Collections.IEnumerable enumerable)
        {
            return enumerable.Cast<object>().ToList();
        }

        return [];
    }

    #region Actions Helper Methods

    /// <summary>
    /// Invokes the Actions class's <c>OnComposeAsync</c> when — and only when — it is overridden.
    /// Returns null (meaning "not composed", proceed with the entity pipeline) when the actions
    /// type keeps the default, doesn't have the hook at all (a hand-written
    /// <c>IPersistentObjectActions&lt;T&gt;</c> implementer), or the override itself returns null.
    /// The override check avoids scaffolding a PersistentObject on every read of every type.
    /// </summary>
    private async Task<PersistentObject?> ComposeViaActionsAsync(Type entityType, Guid objectTypeId, string id)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var composeMethod = ReflectionCache.GetOrAdd<(string Op, Type Actions), MethodInfo?>(
            ("DatabaseAccess.ComposeMethod", actions.GetType()),
            static k =>
            {
                var method = k.Actions.GetMethod("OnComposeAsync");
                // DeclaringType is the most-derived class providing the body; when that is still
                // the open Default base, nothing overrode the hook and there is nothing to invoke.
                if (method is null) return null;
                var declaring = method.DeclaringType;
                if (declaring is { IsGenericType: true }
                    && declaring.GetGenericTypeDefinition() == typeof(Actions.DefaultPersistentObjectActions<>))
                    return null;
                return method;
            });
        if (composeMethod is null)
            return null;

        var scaffold = entityMapper.GetPersistentObject(objectTypeId);
        scaffold.Id = id;

        var task = (Task)composeMethod.Invoke(actions, [new Actions.SparkComposeArgs(id, scaffold)])!;
        await task;
        var composed = (PersistentObject?)task.GetCompletedTaskResult();
        if (composed is null)
            return null;

        composed.Can ??= new PersistentObjectPermissions { Edit = false, Delete = false };
        return composed;
    }

    private async Task<object?> LoadEntityViaActionsAsync(IAsyncDocumentSession session, Type entityType, string id)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var onLoadMethod = GetCachedActionMethod(actions.GetType(), "OnLoadAsync");
        var task = (Task)onLoadMethod.Invoke(actions, [session, id])!;
        await task;
        return task.GetCompletedTaskResult();
    }

    /// <summary>
    /// Dispatches to the Actions class's virtual <c>IsAllowedAsync(string, T)</c> via reflection,
    /// so H-2/H-3 row-level authorization fires regardless of entity type.
    /// </summary>

    private async Task<object> SaveEntityViaActionsAsync(IAsyncDocumentSession session, Type entityType, PersistentObject obj)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var onSaveMethod = GetCachedActionMethod(actions.GetType(), "OnSaveAsync");
        var task = (Task)onSaveMethod.Invoke(actions, [session, obj])!;
        await task;
        return task.GetCompletedTaskResult()!;
    }

    private async Task DeleteEntityViaActionsAsync(IAsyncDocumentSession session, Type entityType, string id)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var onDeleteMethod = GetCachedActionMethod(actions.GetType(), "OnDeleteAsync");
        var task = (Task)onDeleteMethod.Invoke(actions, [session, id])!;
        await task;
    }

    /// <summary>
    /// Cached <c>actionsType.GetMethod(name)</c>. The actions-type+method-name pair is
    /// stable for the AppDomain (an Actions class doesn't grow new methods at runtime),
    /// so a single lookup per pair is sufficient. Throws if the named method is missing —
    /// the action plumbing requires it, so a missing method is a programming error, not
    /// a runtime condition we want to silently swallow.
    /// </summary>
    private static MethodInfo GetCachedActionMethod(Type actionsType, string methodName)
        => ReflectionCache.GetOrAdd<(string Op, Type Actions, string Method), MethodInfo>(
            ("DatabaseAccess.ActionsMethod", actionsType, methodName),
            static k => k.Actions.GetMethod(k.Method)
                ?? throw new InvalidOperationException(
                    $"Actions type '{k.Actions.FullName}' is missing required method '{k.Method}'."));

    #endregion
}
