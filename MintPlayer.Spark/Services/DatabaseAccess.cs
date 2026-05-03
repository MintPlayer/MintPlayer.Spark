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
    [Inject] private readonly IIndexRegistry indexRegistry;
    [Inject] private readonly IServiceProvider serviceProvider;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IReferenceResolver referenceResolver;

    public async Task<T?> GetDocumentAsync<T>(string id) where T : class
    {
        return await session.LoadAsync<T>(id);
    }

    public async Task<IEnumerable<T>> GetDocumentsAsync<T>() where T : class
    {
        return await session.Query<T>().ToListAsync();
    }

    public async Task<IEnumerable<T>> GetDocumentsByObjectTypeIdAsync<T>(Guid objectTypeId) where T : class
    {
        return await session.Query<T>()
            .Where(x => ((PersistentObject)(object)x).ObjectTypeId == objectTypeId)
            .ToListAsync();
    }

    public async Task<T> SaveDocumentAsync<T>(T document) where T : class
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

    public async Task DeleteDocumentAsync<T>(string id) where T : class
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
        var entityType = ResolveType(clrType);
        if (entityType == null) return null;

        // Get reference properties to include
        var referenceProperties = referenceResolver.GetReferenceProperties(entityType);

        // Use actions for loading
        var entity = await LoadEntityViaActionsAsync(session, entityType, id);

        if (entity == null) return null;

        // Row-level read gate. Entity-type "Read" passed; now let the Actions class decide
        // whether this specific instance is visible to the current caller. Returning null
        // here propagates as 404 through the endpoint — same shape as a genuinely missing
        // record, per M-3 (authorized-but-forbidden must be indistinguishable from not-found).
        if (!await IsAllowedEntityViaActionsAsync(entityType, "Read", entity))
            return null;

        // Load included documents for breadcrumb resolution
        var includedDocuments = await referenceResolver.ResolveReferencedDocumentsAsync(session, [entity], referenceProperties);

        var persistentObject = entityMapper.ToPersistentObject(entity, objectTypeId, includedDocuments);
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
        var entityType = ResolveType(clrType);
        if (entityType == null) return [];

        // Check IndexRegistry for projection type - if so, query the index instead of the collection
        Type queryType = entityType;
        string? indexName = null;

        var registration = indexRegistry.GetRegistrationForCollectionType(entityType);
        if (registration?.ProjectionType != null)
        {
            queryType = registration.ProjectionType;
            indexName = registration.IndexName;
        }

        // Get reference properties — fall back to base entity type when projection lacks [Reference]
        var referenceProperties = referenceResolver.GetReferenceProperties(queryType, entityType);

        // Query entities - use index if projection is registered, otherwise query collection
        var entities = (await QueryEntitiesWithIncludesAsync(session, queryType, indexName, referenceProperties)).ToList();

        // Row-level "Query" gate (H-2): after entity-type authz passed, filter the list down
        // to rows the Actions class says the caller may see. For projection queries, the row
        // filter takes the base entity (CarActions typed on Car, not VCar) so we load the
        // matching base docs through the session cache. Callers that need a query-level
        // filter for large collections can override OnQueryAsync directly.
        entities = (await FilterByRowLevelAuthAsync(session, entities, entityType, queryType)).ToList();

        // Referenced documents are now in session cache - extract them
        var includedDocuments = await referenceResolver.ResolveReferencedDocumentsAsync(session, entities, referenceProperties);

        // Convert each entity to PersistentObject with breadcrumb resolution
        return entities.Select(e => entityMapper.ToPersistentObject(e, objectTypeId, includedDocuments));
    }

    /// <summary>
    /// Applies the Actions class's row-level read gate to a materialized list. When the
    /// query ran against a projection type, we load the corresponding base entities from
    /// the session (Raven reuses its cache, so this is cheap for documents already seen)
    /// and evaluate the filter against those — the Actions class is typed on the base
    /// entity, not the projection.
    /// </summary>
    private async Task<IEnumerable<object>> FilterByRowLevelAuthAsync(
        IAsyncDocumentSession session,
        IReadOnlyList<object> entities,
        Type entityType,
        Type queryType)
    {
        if (entities.Count == 0) return entities;

        var idProperty = queryType.GetCachedProperty("Id");
        if (idProperty is null || !idProperty.CanRead) return entities; // Can't resolve IDs — fail open.
        var idGetter = AccessorCache.GetGetter(idProperty);

        var visible = new List<object>(entities.Count);
        foreach (var entity in entities)
        {
            object? subject = entity;
            if (queryType != entityType)
            {
                var id = idGetter(entity)?.ToString();
                if (string.IsNullOrEmpty(id)) { visible.Add(entity); continue; }
                subject = await LoadEntityAsync(session, entityType, id);
                if (subject is null) { visible.Add(entity); continue; }
            }
            if (await IsAllowedEntityViaActionsAsync(entityType, "Query", subject))
                visible.Add(entity);
        }
        return visible;
    }

    public async Task<PersistentObject> SavePersistentObjectAsync(PersistentObject persistentObject)
    {
        var entityTypeDefinition = modelLoader.GetEntityType(persistentObject.ObjectTypeId)
            ?? throw new InvalidOperationException($"Could not find EntityType with ID '{persistentObject.ObjectTypeId}'");

        var action = string.IsNullOrEmpty(persistentObject.Id) ? "New" : "Edit";
        await permissionService.EnsureAuthorizedAsync(action, entityTypeDefinition.Name);

        var entityType = ResolveType(entityTypeDefinition.ClrType)
            ?? throw new InvalidOperationException($"Could not resolve type '{entityTypeDefinition.ClrType}'");

        // Optimistic-concurrency check: if the caller sent an Etag on an existing entity,
        // verify it matches the current server-side change vector before the actions
        // pipeline runs. A mismatch means the entity has been updated since the caller
        // read it — reject instead of silently overwriting. We deliberately open a SIDE
        // session via documentStore here (instead of using the request-scoped session):
        // loading the existing entity for comparison would otherwise pollute change tracking
        // on the main session, conflicting with the actions pipeline's StoreAsync below.
        if (!string.IsNullOrEmpty(persistentObject.Id) && !string.IsNullOrEmpty(persistentObject.Etag))
        {
            using var checkSession = documentStore.OpenAsyncSession();
            var existing = await LoadEntityAsync(checkSession, entityType, persistentObject.Id);
            if (existing is not null)
            {
                var currentEtag = checkSession.Advanced.GetChangeVectorFor(existing);
                if (!string.Equals(currentEtag, persistentObject.Etag, StringComparison.Ordinal))
                    throw new SparkConcurrencyException(persistentObject.Etag, currentEtag);
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
        var entityType = ResolveType(clrType);
        if (entityType == null) return;

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

    private Type? ResolveType(string clrType)
    {
        return ReflectionCache.GetOrAdd<Type?>(
            $"resolveType|{clrType}",
            () =>
            {
                var type = Type.GetType(clrType);
                if (type != null) return type;

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(clrType);
                    if (type != null) return type;
                }

                return null;
            });
    }

    /// <summary>
    /// Queries entities and uses .Include() for all reference properties so that
    /// referenced documents are loaded in a single database call.
    /// When indexName is provided, queries the RavenDB index instead of the collection.
    /// </summary>
    private async Task<IEnumerable<object>> QueryEntitiesWithIncludesAsync(
        IAsyncDocumentSession session,
        Type entityType,
        string? indexName,
        List<(PropertyInfo Property, ReferenceAttribute Attribute)> referenceProperties)
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

        // Chain .Include(propertyName) so referenced documents are loaded in the same round-trip
        if (query != null && referenceProperties.Count > 0)
        {
            query = referenceResolver.ApplyIncludes(query, referenceProperties);
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
    private async Task<bool> IsAllowedEntityViaActionsAsync(Type entityType, string action, object entity)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var actionsType = actions.GetType();
        var method = ReflectionCache.GetOrAdd<(string Op, Type Actions, Type Entity), MethodInfo?>(
            ("DatabaseAccess.IsAllowedAsync", actionsType, entityType),
            static k => k.Actions.GetMethod("IsAllowedAsync", [typeof(string), k.Entity]));
        if (method is null)
            return true; // Unknown shape — fail open rather than dropping valid rows.
        var task = (Task)method.Invoke(actions, [action, entity])!;
        await task;
        return (bool)task.GetCompletedTaskResult()!;
    }

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
