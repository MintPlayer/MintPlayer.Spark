using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
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
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IActionsResolver actionsResolver;
    [Inject] private readonly IIndexRegistry indexRegistry;
    [Inject] private readonly IServiceProvider serviceProvider;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IReferenceResolver referenceResolver;

    public async Task<T?> GetDocumentAsync<T>(string id) where T : class
    {
        using var session = documentStore.OpenAsyncSession();
        return await session.LoadAsync<T>(id);
    }

    public async Task<IEnumerable<T>> GetDocumentsAsync<T>() where T : class
    {
        using var session = documentStore.OpenAsyncSession();
        return await session.Query<T>().ToListAsync();
    }

    public async Task<IEnumerable<T>> GetDocumentsByObjectTypeIdAsync<T>(Guid objectTypeId) where T : class
    {
        using var session = documentStore.OpenAsyncSession();
        return await session.Query<T>()
            .Where(x => ((PersistentObject)(object)x).ObjectTypeId == objectTypeId)
            .ToListAsync();
    }

    public async Task<T> SaveDocumentAsync<T>(T document) where T : class
    {
        using var session = documentStore.OpenAsyncSession();
        await session.StoreAsync(document);
        await session.SaveChangesAsync();

        // If this is a replicated entity, also broadcast the changes to the owner module
        var interceptor = serviceProvider.GetService<ISyncActionInterceptor>();
        if (interceptor != null && interceptor.IsReplicated(typeof(T)))
        {
            var idProperty = typeof(T).GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
            var documentId = idProperty?.GetValue(document)?.ToString();
            await interceptor.HandleSaveAsync(document, documentId);
        }

        return document;
    }

    public async Task DeleteDocumentAsync<T>(string id) where T : class
    {
        using var session = documentStore.OpenAsyncSession();
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

        using var session = documentStore.OpenAsyncSession();

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

        using var session = documentStore.OpenAsyncSession();

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

        var idProperty = queryType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        if (idProperty is null) return entities; // Can't resolve IDs — fail open.

        var visible = new List<object>(entities.Count);
        foreach (var entity in entities)
        {
            object? subject = entity;
            if (queryType != entityType)
            {
                var id = idProperty.GetValue(entity)?.ToString();
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

        using var session = documentStore.OpenAsyncSession();

        // Optimistic-concurrency check: if the caller sent an Etag on an existing entity,
        // verify it matches the current server-side change vector before the actions
        // pipeline runs. A mismatch means the entity has been updated since the caller
        // read it — reject instead of silently overwriting. We load into a side session so
        // the tracking on the main session is clean for the actions pipeline's StoreAsync.
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
        var idProperty = entityType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        var generatedId = idProperty?.GetValue(savedEntity)?.ToString();

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

        using var session = documentStore.OpenAsyncSession();

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
        // Use reflection to call the generic LoadAsync<T> method
        var method = typeof(IAsyncDocumentSession).GetMethod(nameof(IAsyncDocumentSession.LoadAsync), [typeof(string), typeof(CancellationToken)]);
        var genericMethod = method?.MakeGenericMethod(entityType);
        var task = genericMethod?.Invoke(session, [id, CancellationToken.None]) as Task;

        if (task == null) return null;

        await task;

        // Get the result from the task
        var resultProperty = task.GetType().GetProperty("Result");
        return resultProperty?.GetValue(task);
    }

    private async Task<IEnumerable<object>> QueryEntitiesAsync(IAsyncDocumentSession session, Type entityType)
    {
        // Use Query<T> directly on the session (not via Advanced)
        // Query(string indexName, string collectionName, bool isMapReduce) with 1 generic param and 3 regular params
        var sessionType = session.GetType();

        var queryMethod = sessionType.GetMethods()
            .FirstOrDefault(m => m.Name == "Query"
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 3);

        if (queryMethod == null) return [];

        var genericQueryMethod = queryMethod.MakeGenericMethod(entityType);
        // Pass null for indexName, null for collectionName, false for isMapReduce
        var query = genericQueryMethod.Invoke(session, [null, null, false]);

        if (query == null) return [];

        // Call ToListAsync on the IRavenQueryable<T>
        // ToListAsync is an extension method in Raven.Client.Documents.Linq.LinqExtensions
        var toListMethod = typeof(LinqExtensions).GetMethods()
            .FirstOrDefault(m => m.Name == nameof(LinqExtensions.ToListAsync)
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 2);

        if (toListMethod == null) return [];

        var genericToListMethod = toListMethod.MakeGenericMethod(entityType);
        var task = genericToListMethod.Invoke(null, [query, CancellationToken.None]) as Task;

        if (task == null) return [];

        await task;

        var resultProperty = task.GetType().GetProperty("Result");
        var result = resultProperty?.GetValue(task);

        if (result is System.Collections.IEnumerable enumerable)
        {
            return enumerable.Cast<object>().ToList();
        }

        return [];
    }

    private Type? ResolveType(string clrType)
    {
        // First try the standard Type.GetType which works for assembly-qualified names
        var type = Type.GetType(clrType);
        if (type != null) return type;

        // Search through all loaded assemblies
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(clrType);
            if (type != null) return type;
        }

        return null;
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
        var queryMethod = sessionType.GetMethods()
            .FirstOrDefault(m => m.Name == "Query"
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 3);

        if (queryMethod == null)
            return [];

        var genericQueryMethod = queryMethod.MakeGenericMethod(entityType);

        // Pass indexName if querying an index, null for collection query
        // RavenDB converts underscores to slashes in index names (e.g., "People_Overview" -> "People/Overview")
        var ravenIndexName = indexName?.Replace("_", "/");
        query = genericQueryMethod.Invoke(session, [ravenIndexName, null, false]);

        if (query == null)
            return [];

        // When querying an index, use ProjectInto<T>() to project from stored fields
        // This ensures computed/stored fields like FullName are populated from the index
        // ProjectInto is an extension method on IQueryable (non-generic) in LinqExtensions
        if (!string.IsNullOrEmpty(indexName))
        {
            var projectIntoMethod = typeof(LinqExtensions).GetMethods()
                .FirstOrDefault(m => m.Name == "ProjectInto"
                    && m.IsGenericMethod
                    && m.GetGenericArguments().Length == 1
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(IQueryable));

            if (projectIntoMethod != null)
            {
                var genericProjectIntoMethod = projectIntoMethod.MakeGenericMethod(entityType);
                query = genericProjectIntoMethod.Invoke(null, [query])!;
            }
        }

        // Chain .Include(propertyName) so referenced documents are loaded in the same round-trip
        if (query != null && referenceProperties.Count > 0)
        {
            query = referenceResolver.ApplyIncludes(query, referenceProperties);
        }

        // Call ToListAsync on the query
        var toListMethod = typeof(LinqExtensions).GetMethods()
            .FirstOrDefault(m => m.Name == nameof(LinqExtensions.ToListAsync)
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 2);

        if (toListMethod == null)
            return [];

        var genericToListMethod = toListMethod.MakeGenericMethod(entityType);
        var task = genericToListMethod.Invoke(null, [query, CancellationToken.None]) as Task;

        if (task == null)
            return [];

        await task;

        var resultProperty = task.GetType().GetProperty("Result");
        var result = resultProperty?.GetValue(task);

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
        var onLoadMethod = actions.GetType().GetMethod("OnLoadAsync")!;
        var task = (Task)onLoadMethod.Invoke(actions, [session, id])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    /// <summary>
    /// Dispatches to the Actions class's virtual <c>IsAllowedAsync(string, T)</c> via reflection,
    /// so H-2/H-3 row-level authorization fires regardless of entity type.
    /// </summary>
    private async Task<bool> IsAllowedEntityViaActionsAsync(Type entityType, string action, object entity)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var method = actions.GetType().GetMethod("IsAllowedAsync", [typeof(string), entityType]);
        if (method is null)
            return true; // Unknown shape — fail open rather than dropping valid rows.
        var task = (Task)method.Invoke(actions, [action, entity])!;
        await task;
        return (bool)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private async Task<IEnumerable<object>> QueryEntitiesViaActionsAsync(IAsyncDocumentSession session, Type entityType)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var onQueryMethod = actions.GetType().GetMethod("OnQueryAsync")!;
        var task = (Task)onQueryMethod.Invoke(actions, [session])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task);

        if (result is System.Collections.IEnumerable enumerable)
        {
            return enumerable.Cast<object>().ToList();
        }

        return [];
    }

    private async Task<object> SaveEntityViaActionsAsync(IAsyncDocumentSession session, Type entityType, PersistentObject obj)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var onSaveMethod = actions.GetType().GetMethod("OnSaveAsync")!;
        var task = (Task)onSaveMethod.Invoke(actions, [session, obj])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private async Task DeleteEntityViaActionsAsync(IAsyncDocumentSession session, Type entityType, string id)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var onDeleteMethod = actions.GetType().GetMethod("OnDeleteAsync")!;
        var task = (Task)onDeleteMethod.Invoke(actions, [session, id])!;
        await task;
    }

    #endregion
}
