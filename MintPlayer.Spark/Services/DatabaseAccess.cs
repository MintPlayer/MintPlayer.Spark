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

    public async Task<IEnumerable<T>> GetDocumentsByTypeAsync<T>(string clrType) where T : class
    {
        using var session = documentStore.OpenAsyncSession();
        return await session.Query<T>()
            .Where(x => ((PersistentObject)(object)x).ClrType == clrType)
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

    public async Task<PersistentObject?> GetPersistentObjectAsync(string clrType, string id)
    {
        var entityType = Type.GetType(clrType);
        if (entityType == null) return null;

        var collectionName = CollectionHelper.GetCollectionName(clrType);
        var documentId = $"{collectionName}/{id}";

        using var session = documentStore.OpenAsyncSession();
        var entity = await LoadEntityAsync(session, entityType, documentId);

        if (entity == null) return null;

        return entityMapper.ToPersistentObject(entity, clrType);
    }

    public async Task<IEnumerable<PersistentObject>> GetPersistentObjectsAsync(string clrType)
    {
        var entityType = Type.GetType(clrType);
        if (entityType == null) return [];

        using var session = documentStore.OpenAsyncSession();
        var entities = await QueryEntitiesAsync(session, entityType);

        return entities.Select(e => entityMapper.ToPersistentObject(e, clrType));
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

    public async Task DeletePersistentObjectAsync(string clrType, string id)
    {
        var collectionName = CollectionHelper.GetCollectionName(clrType);
        var documentId = $"{collectionName}/{id}";

        using var session = documentStore.OpenAsyncSession();
        session.Delete(documentId);
        await session.SaveChangesAsync();
    }

    private static async Task<object?> LoadEntityAsync(IAsyncDocumentSession session, Type entityType, string id)
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

    private static async Task<IEnumerable<object>> QueryEntitiesAsync(IAsyncDocumentSession session, Type entityType)
    {
        // Use reflection to call the generic Query<T> method
        var queryMethod = typeof(IAsyncDocumentSession).GetMethod(nameof(IAsyncDocumentSession.Query), Type.EmptyTypes);
        var genericQueryMethod = queryMethod?.MakeGenericMethod(entityType);
        var query = genericQueryMethod?.Invoke(session, null);

        if (query == null) return [];

        // Call ToListAsync on the query
        var toListMethod = typeof(LinqExtensions).GetMethods()
            .FirstOrDefault(m => m.Name == nameof(LinqExtensions.ToListAsync) && m.GetParameters().Length == 2);
        var genericToListMethod = toListMethod?.MakeGenericMethod(entityType);
        var task = genericToListMethod?.Invoke(null, [query, CancellationToken.None]) as Task;

        if (task == null) return [];

        await task;

        var resultProperty = task.GetType().GetProperty("Result");
        var result = resultProperty?.GetValue(task);

        if (result is System.Collections.IEnumerable enumerable)
        {
            return enumerable.Cast<object>();
        }

        return [];
    }
}
