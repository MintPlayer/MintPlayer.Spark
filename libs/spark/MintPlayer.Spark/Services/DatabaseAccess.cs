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

        // The load contract (#324): id in, page out — the type's Actions class owns everything
        // through OnLoadAsync(id, parent). For an entity-backed type the default base runs the
        // entity pipeline (document load, collection guard, row security, breadcrumbs, mapping,
        // redaction, etag); a JSON-only virtual type's name-resolved actions scaffold via
        // IManager and fill the values directly. What comes back is what the page renders; null
        // is 404.
        var entityType = typeResolver.Resolve(entityTypeDefinition.ClrType);
        if (entityType == null)
            return await LoadVirtualObjectViaActionsAsync(entityTypeDefinition, id);

        var actions = actionsResolver.ResolveForType(entityType);
        var onLoadMethod = GetCachedActionMethod(actions.GetType(), "OnLoadAsync");
        var task = (Task)onLoadMethod.Invoke(actions, [id, null])!;
        await task;
        return (PersistentObject?)task.GetCompletedTaskResult();
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
    /// The read path for a JSON-only virtual type: resolve <c>{Name}Actions</c> by the model
    /// type's name (there is no CLR type to resolve over) and invoke its load hook — duck-typed
    /// with the exact same signature every actions class has, no base class required:
    /// <code>public Task&lt;PersistentObject?&gt; OnLoadAsync(string id, PersistentObject? parent)</code>
    /// The class scaffolds its own object (the <c>IManager.GetPersistentObject</c> idiom dialogs
    /// already use), fills values and <see cref="PersistentObject.Breadcrumb"/> (the page title),
    /// and returns it — free to ignore the id. The result is served read-only (<c>Can</c> forced
    /// to none unless the hook set it) — anything interactive on such a page is a custom action
    /// with its own authorization.
    /// <para>
    /// No actions class, or no <c>OnLoadAsync</c> on it, means the type has no page: null → 404.
    /// A method named <c>OnLoadAsync</c> whose shape doesn't match throws loudly instead of
    /// silently 404ing — the contract is reflective, so this is where a typo surfaces.
    /// </para>
    /// </summary>
    private async Task<PersistentObject?> LoadVirtualObjectViaActionsAsync(
        EntityTypeDefinition entityTypeDefinition, string id)
    {
        var actions = actionsResolver.ResolveByEntityName(entityTypeDefinition.Name);
        if (actions is null)
            return null;

        var loadMethod = ReflectionCache.GetOrAdd<(string Op, Type Actions), MethodInfo?>(
            ("DatabaseAccess.VirtualLoadMethod", actions.GetType()),
            static k =>
            {
                var method = k.Actions.GetMethod("OnLoadAsync", [typeof(string), typeof(PersistentObject)]);
                if (method is not null)
                {
                    if (method.ReturnType != typeof(Task<PersistentObject?>))
                        throw new InvalidOperationException(
                            $"'{k.Actions.FullName}.OnLoadAsync' must return Task<PersistentObject?>. " +
                            $"Expected: 'Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)'.");
                    return method;
                }
                if (k.Actions.GetMethods().Any(m => m.Name == "OnLoadAsync"))
                    throw new InvalidOperationException(
                        $"'{k.Actions.FullName}' has an OnLoadAsync that doesn't match the load contract. " +
                        $"Expected: 'Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)'.");
                return null;
            });
        if (loadMethod is null)
            return null;

        var task = (Task)loadMethod.Invoke(actions, [id, null])!;
        await task;
        var obj = (PersistentObject?)task.GetCompletedTaskResult();
        if (obj is null)
            return null;

        // The hook only fills values; the framework squares the envelope: the page answers to
        // the id it was requested as (unless the hook chose another), titles itself from the
        // model's breadcrumb template over the values the hook just filled (unless the hook set
        // one), and is read-only unless the hook said otherwise.
        obj.Id ??= id;
        obj.Breadcrumb ??= RenderVirtualBreadcrumb(entityTypeDefinition.Breadcrumb, obj);
        obj.Can ??= new PersistentObjectPermissions { Edit = false, Delete = false };
        return obj;
    }

    /// <summary>
    /// The virtual-type counterpart of breadcrumb resolution: no entity exists, so the model's
    /// template renders over the returned object's attribute values. Reference placeholders
    /// can't resolve here (nothing to follow) and render as empty.
    /// </summary>
    private static string? RenderVirtualBreadcrumb(string? template, PersistentObject obj)
    {
        if (string.IsNullOrEmpty(template))
            return null;

        var rendered = string.Concat(Breadcrumb.BreadcrumbTemplate.Parse(template).Select(token => token switch
        {
            Breadcrumb.LiteralToken literal => literal.Text,
            Breadcrumb.FieldToken field =>
                obj.Attributes.FirstOrDefault(a => a.Name == field.AttributeName)?.Value?.ToString() ?? string.Empty,
            _ => string.Empty,
        }));
        return string.IsNullOrWhiteSpace(rendered) ? null : rendered;
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
