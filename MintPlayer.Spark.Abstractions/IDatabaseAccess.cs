namespace MintPlayer.Spark.Abstractions;

public interface IDatabaseAccess
{
    Task<T?> GetDocumentAsync<T>(string id) where T : class;
    Task<IEnumerable<T>> GetDocumentsAsync<T>() where T : class;
    Task<IEnumerable<T>> GetDocumentsByObjectTypeIdAsync<T>(Guid objectTypeId) where T : class;
    Task<T> SaveDocumentAsync<T>(T document) where T : class;
    Task DeleteDocumentAsync<T>(string id) where T : class;

    // PersistentObject-specific methods that handle entity mapping
    Task<PersistentObject?> GetPersistentObjectAsync(Guid objectTypeId, string id);
    Task<IEnumerable<PersistentObject>> GetPersistentObjectsAsync(Guid objectTypeId);
    Task<PersistentObject> SavePersistentObjectAsync(PersistentObject persistentObject);
    Task DeletePersistentObjectAsync(Guid objectTypeId, string id);
}
