using System.Reflection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Helpers;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Services;

[Register(typeof(IDatabaseAccess), ServiceLifetime.Scoped, "AddSparkServices")]
internal partial class DatabaseAccess : IDatabaseAccess
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ICollectionHelper collectionHelper;

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

        var collectionName = collectionHelper.GetCollectionName(clrType);
        var documentId = $"{collectionName}/{id}";

        using var session = documentStore.OpenAsyncSession();

        // Get reference properties to include
        var referenceProperties = GetReferenceProperties(entityType);

        var entity = await LoadEntityWithIncludesAsync(session, entityType, documentId, referenceProperties);

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
        var entities = await QueryEntitiesAsync(session, entityType);

        return entities.Select(e => entityMapper.ToPersistentObject(e, objectTypeId));
    }

    public async Task<PersistentObject> SavePersistentObjectAsync(PersistentObject persistentObject)
    {
        var entity = entityMapper.ToEntity(persistentObject);

        using var session = documentStore.OpenAsyncSession();
        await session.StoreAsync(entity);
        await session.SaveChangesAsync();

        // Get the generated ID from the entity
        var idProperty = entity.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        var generatedId = idProperty?.GetValue(entity)?.ToString();

        persistentObject.Id = generatedId;
        return persistentObject;
    }

    public async Task DeletePersistentObjectAsync(Guid objectTypeId, string id)
    {
        var entityTypeDefinition = modelLoader.GetEntityType(objectTypeId);
        if (entityTypeDefinition == null) return;

        var clrType = entityTypeDefinition.ClrType;
        var collectionName = collectionHelper.GetCollectionName(clrType);
        var documentId = $"{collectionName}/{id}";

        using var session = documentStore.OpenAsyncSession();
        session.Delete(documentId);
        await session.SaveChangesAsync();
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
}
