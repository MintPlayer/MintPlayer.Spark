using System.Reflection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Services;

[Register(typeof(IDatabaseAccess), ServiceLifetime.Scoped)]
internal partial class DatabaseAccess : IDatabaseAccess
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IActionsResolver actionsResolver;
    [Inject] private readonly IIndexRegistry indexRegistry;

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
        return document;
    }

    public async Task DeleteDocumentAsync<T>(string id) where T : class
    {
        using var session = documentStore.OpenAsyncSession();
        session.Delete(id);
        await session.SaveChangesAsync();
    }

    // PersistentObject-specific methods that handle entity mapping

    public async Task<PersistentObject?> GetPersistentObjectAsync(Guid objectTypeId, string id)
    {
        var entityTypeDefinition = modelLoader.GetEntityType(objectTypeId);
        if (entityTypeDefinition == null) return null;

        var clrType = entityTypeDefinition.ClrType;
        var entityType = ResolveType(clrType);
        if (entityType == null) return null;

        using var session = documentStore.OpenAsyncSession();

        // Get reference properties to include
        var referenceProperties = GetReferenceProperties(entityType);

        // Use actions for loading
        var entity = await LoadEntityViaActionsAsync(session, entityType, id);

        if (entity == null) return null;

        // Load included documents for breadcrumb resolution
        var includedDocuments = await LoadIncludedDocumentsAsync(session, entity, referenceProperties);

        return entityMapper.ToPersistentObject(entity, objectTypeId, includedDocuments);
    }

    public async Task<IEnumerable<PersistentObject>> GetPersistentObjectsAsync(Guid objectTypeId)
    {
        var entityTypeDefinition = modelLoader.GetEntityType(objectTypeId);
        if (entityTypeDefinition == null) return [];

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

        // Get reference properties from the type being queried (queryType, not entityType)
        // When querying an index, the projection type (e.g., VPerson) may not have the same reference properties
        var referenceProperties = GetReferenceProperties(queryType);

        // Query entities - use index if projection is registered, otherwise query collection
        var entities = (await QueryEntitiesWithIncludesAsync(session, queryType, indexName, referenceProperties)).ToList();

        // Referenced documents are now in session cache - extract them
        // Only do this if we have reference properties on the queried type
        var includedDocuments = referenceProperties.Count > 0
            ? await ExtractIncludedDocumentsFromSessionAsync(session, entities, referenceProperties)
            : new Dictionary<string, object>();

        // Convert each entity to PersistentObject with breadcrumb resolution
        return entities.Select(e => entityMapper.ToPersistentObject(e, objectTypeId, includedDocuments));
    }

    public async Task<PersistentObject> SavePersistentObjectAsync(PersistentObject persistentObject)
    {
        var entity = entityMapper.ToEntity(persistentObject);
        var entityType = entity.GetType();

        using var session = documentStore.OpenAsyncSession();

        // Use actions for saving (includes before/after hooks)
        var savedEntity = await SaveEntityViaActionsAsync(session, entityType, entity);

        // Get the generated ID from the entity
        var idProperty = entityType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        var generatedId = idProperty?.GetValue(savedEntity)?.ToString();

        persistentObject.Id = generatedId;
        return persistentObject;
    }

    public async Task DeletePersistentObjectAsync(Guid objectTypeId, string id)
    {
        var entityTypeDefinition = modelLoader.GetEntityType(objectTypeId);
        if (entityTypeDefinition == null) return;

        var clrType = entityTypeDefinition.ClrType;
        var entityType = ResolveType(clrType);
        if (entityType == null) return;

        using var session = documentStore.OpenAsyncSession();

        // Use actions for deleting (includes before hook)
        await DeleteEntityViaActionsAsync(session, entityType, id);
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

    private List<(PropertyInfo Property, ReferenceAttribute Attribute)> GetReferenceProperties(Type entityType)
    {
        return entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Property: p, Attribute: p.GetCustomAttribute<ReferenceAttribute>()))
            .Where(x => x.Attribute != null)
            .Select(x => (x.Property, x.Attribute!))
            .ToList();
    }

    private async Task<object?> LoadEntityWithIncludesAsync(
        IAsyncDocumentSession session,
        Type entityType,
        string documentId,
        List<(PropertyInfo Property, ReferenceAttribute Attribute)> referenceProperties)
    {
        // For simplicity, just load the entity normally
        // RavenDB will automatically track any subsequently loaded documents
        return await LoadEntityAsync(session, entityType, documentId);
    }

    private async Task<Dictionary<string, object>> LoadIncludedDocumentsAsync(
        IAsyncDocumentSession session,
        object entity,
        List<(PropertyInfo Property, ReferenceAttribute Attribute)> referenceProperties)
    {
        var includedDocuments = new Dictionary<string, object>();

        foreach (var (property, refAttr) in referenceProperties)
        {
            var refId = property.GetValue(entity) as string;
            if (string.IsNullOrEmpty(refId)) continue;

            var targetType = refAttr.TargetType;
            var referencedEntity = await LoadEntityAsync(session, targetType, refId);

            if (referencedEntity != null)
            {
                includedDocuments[refId] = referencedEntity;
            }
        }

        return includedDocuments;
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

        // Chain .Include(propertyName) for each reference property
        // RavenDB's IRavenQueryable<T> has Include(string path) method
        foreach (var (property, _) in referenceProperties)
        {
            if (query == null) break;

            var queryType = query.GetType();

            // Look for Include method that takes a string path
            var includeMethod = queryType.GetMethods()
                .FirstOrDefault(m => m.Name == "Include"
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(string));

            if (includeMethod != null)
            {
                query = includeMethod.Invoke(query, [property.Name]);
            }
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

    /// <summary>
    /// Extracts referenced documents from the session cache (they were loaded via .Include()).
    /// Since they're already in the session, LoadAsync returns immediately without a database call.
    /// </summary>
    private async Task<Dictionary<string, object>> ExtractIncludedDocumentsFromSessionAsync(
        IAsyncDocumentSession session,
        IEnumerable<object> entities,
        List<(PropertyInfo Property, ReferenceAttribute Attribute)> referenceProperties)
    {
        var includedDocuments = new Dictionary<string, object>();

        if (!referenceProperties.Any())
            return includedDocuments;

        // Collect all unique reference IDs
        var refIdsByType = new Dictionary<Type, HashSet<string>>();

        foreach (var entity in entities)
        {
            foreach (var (property, refAttr) in referenceProperties)
            {
                var refId = property.GetValue(entity) as string;
                if (string.IsNullOrEmpty(refId)) continue;

                var targetType = refAttr.TargetType;
                if (!refIdsByType.ContainsKey(targetType))
                {
                    refIdsByType[targetType] = [];
                }
                refIdsByType[targetType].Add(refId);
            }
        }

        // Load from session cache (no database calls - documents were included in the query)
        foreach (var (targetType, refIds) in refIdsByType)
        {
            foreach (var refId in refIds)
            {
                // This LoadAsync returns from session cache, not from database
                var referencedEntity = await LoadEntityAsync(session, targetType, refId);
                if (referencedEntity != null)
                {
                    includedDocuments[refId] = referencedEntity;
                }
            }
        }

        return includedDocuments;
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

    private async Task<object> SaveEntityViaActionsAsync(IAsyncDocumentSession session, Type entityType, object entity)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var onSaveMethod = actions.GetType().GetMethod("OnSaveAsync")!;
        var task = (Task)onSaveMethod.Invoke(actions, [session, entity])!;
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
