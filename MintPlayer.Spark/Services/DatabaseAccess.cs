using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Storage;
using System.Reflection;

namespace MintPlayer.Spark.Services;

[Register(typeof(IDatabaseAccess), ServiceLifetime.Scoped)]
internal partial class DatabaseAccess : IDatabaseAccess
{
    [Inject] private readonly ISparkSessionFactory sessionFactory;
    [Inject] private readonly ISparkStorageProvider storageProvider;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IActionsResolver actionsResolver;
    [Inject] private readonly IIndexRegistry indexRegistry;
    [Inject] private readonly IServiceProvider serviceProvider;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IReferenceResolver referenceResolver;

    public async Task<T?> GetDocumentAsync<T>(string id) where T : class
    {
        using var session = sessionFactory.OpenSession();
        return await session.LoadAsync<T>(id);
    }

    public async Task<IEnumerable<T>> GetDocumentsAsync<T>() where T : class
    {
        using var session = sessionFactory.OpenSession();
        return await storageProvider.ToListAsync(session.Query<T>());
    }

    public async Task<IEnumerable<T>> GetDocumentsByObjectTypeIdAsync<T>(Guid objectTypeId) where T : class
    {
        using var session = sessionFactory.OpenSession();
        return await storageProvider.ToListAsync(
            session.Query<T>().Where(x => ((PersistentObject)(object)x).ObjectTypeId == objectTypeId));
    }

    public async Task<T> SaveDocumentAsync<T>(T document) where T : class
    {
        using var session = sessionFactory.OpenSession();
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
        using var session = sessionFactory.OpenSession();
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

        using var session = sessionFactory.OpenSession();

        // Get reference properties to include
        var referenceProperties = referenceResolver.GetReferenceProperties(entityType);

        // Use actions for loading
        var entity = await LoadEntityViaActionsAsync(session, entityType, id);

        if (entity == null) return null;

        // Load included documents for breadcrumb resolution
        var includedDocuments = await referenceResolver.ResolveReferencedDocumentsAsync(session, [entity], referenceProperties);

        return entityMapper.ToPersistentObject(entity, objectTypeId, includedDocuments);
    }

    public async Task<IEnumerable<PersistentObject>> GetPersistentObjectsAsync(Guid objectTypeId)
    {
        var entityTypeDefinition = modelLoader.GetEntityType(objectTypeId);
        if (entityTypeDefinition == null) return [];

        await permissionService.EnsureAuthorizedAsync("Query", entityTypeDefinition.Name);

        var clrType = entityTypeDefinition.ClrType;
        var entityType = ResolveType(clrType);
        if (entityType == null) return [];

        using var session = sessionFactory.OpenSession();

        // Check IndexRegistry for projection type - if so, query the index instead of the collection
        Type queryType = entityType;
        Type? indexType = null;

        var registration = indexRegistry.GetRegistrationForCollectionType(entityType);
        if (registration?.ProjectionType != null)
        {
            queryType = registration.ProjectionType;
            indexType = registration.IndexType;
        }

        // Get reference properties — fall back to base entity type when projection lacks [Reference]
        var referenceProperties = referenceResolver.GetReferenceProperties(queryType, entityType);

        // Query entities - use index if projection is registered, otherwise query collection
        var entities = (await QueryEntitiesWithIncludesAsync(session, queryType, indexType, referenceProperties)).ToList();

        // Referenced documents are now in session cache - extract them
        var includedDocuments = await referenceResolver.ResolveReferencedDocumentsAsync(session, entities, referenceProperties);

        // Convert each entity to PersistentObject with breadcrumb resolution
        return entities.Select(e => entityMapper.ToPersistentObject(e, objectTypeId, includedDocuments));
    }

    public async Task<PersistentObject> SavePersistentObjectAsync(PersistentObject persistentObject)
    {
        var entityTypeDefinition = modelLoader.GetEntityType(persistentObject.ObjectTypeId)
            ?? throw new InvalidOperationException($"Could not find EntityType with ID '{persistentObject.ObjectTypeId}'");

        var action = string.IsNullOrEmpty(persistentObject.Id) ? "New" : "Edit";
        await permissionService.EnsureAuthorizedAsync(action, entityTypeDefinition.Name);

        var entityType = ResolveType(entityTypeDefinition.ClrType)
            ?? throw new InvalidOperationException($"Could not resolve type '{entityTypeDefinition.ClrType}'");

        using var session = sessionFactory.OpenSession();

        // Pass PO directly to actions — entity mapping happens inside the actions pipeline
        var savedEntity = await SaveEntityViaActionsAsync(session, entityType, persistentObject);

        // Get the generated ID from the entity
        var idProperty = entityType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        var generatedId = idProperty?.GetValue(savedEntity)?.ToString();

        persistentObject.Id = generatedId;

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

        using var session = sessionFactory.OpenSession();

        // Delete locally first (includes before hook)
        await DeleteEntityViaActionsAsync(session, entityType, id);

        // If this is a replicated entity, also notify the owner module
        var interceptor = serviceProvider.GetService<ISyncActionInterceptor>();
        if (interceptor != null && interceptor.IsReplicated(entityType))
        {
            await interceptor.HandleDeleteAsync(entityType, id);
        }
    }

    private async Task<object?> LoadEntityAsync(ISparkSession session, Type entityType, string id)
    {
        // Use reflection to call the generic LoadAsync<T> method
        var method = typeof(ISparkSession).GetMethod(nameof(ISparkSession.LoadAsync), [typeof(string)]);
        var genericMethod = method?.MakeGenericMethod(entityType);
        var task = genericMethod?.Invoke(session, [id]) as Task;

        if (task == null) return null;

        await task;

        // Get the result from the task
        var resultProperty = task.GetType().GetProperty("Result");
        return resultProperty?.GetValue(task);
    }

    private async Task<IEnumerable<object>> QueryEntitiesAsync(ISparkSession session, Type entityType)
    {
        // Use reflection to call the generic Query<T>() method on ISparkSession
        var queryMethod = typeof(ISparkSession).GetMethods()
            .FirstOrDefault(m => m.Name == nameof(ISparkSession.Query)
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 0);

        if (queryMethod == null) return [];

        var genericQueryMethod = queryMethod.MakeGenericMethod(entityType);
        var query = genericQueryMethod.Invoke(session, []);

        if (query == null) return [];

        // Call storageProvider.ToListAsync on the IQueryable<T>
        var toListMethod = typeof(ISparkStorageProvider).GetMethods()
            .FirstOrDefault(m => m.Name == nameof(ISparkStorageProvider.ToListAsync)
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 2);

        if (toListMethod == null) return [];

        var genericToListMethod = toListMethod.MakeGenericMethod(entityType);
        var task = genericToListMethod.Invoke(storageProvider, [query, CancellationToken.None]) as Task;

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
    /// When indexName is provided, queries the index instead of the collection.
    /// </summary>
    private async Task<IEnumerable<object>> QueryEntitiesWithIncludesAsync(
        ISparkSession session,
        Type entityType,
        Type? indexType,
        List<(PropertyInfo Property, ReferenceAttribute Attribute)> referenceProperties)
    {
        object? query;

        if (indexType != null)
        {
            // Use ISparkSession.Query<T>(Type indexType) — maps to session.Query<T, TIndex>()
            var queryMethod = typeof(ISparkSession).GetMethods()
                .FirstOrDefault(m => m.Name == nameof(ISparkSession.Query)
                    && m.GetGenericArguments().Length == 1
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(Type));

            if (queryMethod == null)
                return [];

            query = queryMethod.MakeGenericMethod(entityType).Invoke(session, [indexType]);
        }
        else
        {
            // Use ISparkSession.Query<T>() without index
            var queryMethod = typeof(ISparkSession).GetMethods()
                .FirstOrDefault(m => m.Name == nameof(ISparkSession.Query)
                    && m.GetGenericArguments().Length == 1
                    && m.GetParameters().Length == 0);

            if (queryMethod == null)
                return [];

            query = queryMethod.MakeGenericMethod(entityType).Invoke(session, []);
        }

        if (query == null)
            return [];

        // Chain .Include(propertyName) so referenced documents are loaded in the same round-trip
        if (query != null && referenceProperties.Count > 0)
        {
            query = referenceResolver.ApplyIncludes(query, referenceProperties);
        }

        // Call storageProvider.ToListAsync on the query
        var toListMethod = typeof(ISparkStorageProvider).GetMethods()
            .FirstOrDefault(m => m.Name == nameof(ISparkStorageProvider.ToListAsync)
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 2);

        if (toListMethod == null)
            return [];

        var genericToListMethod = toListMethod.MakeGenericMethod(entityType);
        var task = genericToListMethod.Invoke(storageProvider, [query, CancellationToken.None]) as Task;

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

    private async Task<object?> LoadEntityViaActionsAsync(ISparkSession session, Type entityType, string id)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var onLoadMethod = actions.GetType().GetMethod("OnLoadAsync")!;
        var task = (Task)onLoadMethod.Invoke(actions, [session, id])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private async Task<IEnumerable<object>> QueryEntitiesViaActionsAsync(ISparkSession session, Type entityType)
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

    private async Task<object> SaveEntityViaActionsAsync(ISparkSession session, Type entityType, PersistentObject obj)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var onSaveMethod = actions.GetType().GetMethod("OnSaveAsync")!;
        var task = (Task)onSaveMethod.Invoke(actions, [session, obj])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private async Task DeleteEntityViaActionsAsync(ISparkSession session, Type entityType, string id)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var onDeleteMethod = actions.GetType().GetMethod("OnDeleteAsync")!;
        var task = (Task)onDeleteMethod.Invoke(actions, [session, id])!;
        await task;
    }

    #endregion
}
