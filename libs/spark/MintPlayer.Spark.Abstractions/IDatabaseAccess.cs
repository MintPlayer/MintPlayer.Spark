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
    /// <summary>
    /// Authorizes a save without performing it, so a caller can be refused before the request is
    /// validated. <see cref="SavePersistentObjectAsync"/> calls this itself — asking early does not
    /// move the decision out of the chokepoint.
    /// </summary>
    Task EnsureSaveAuthorizedAsync(PersistentObject persistentObject);

    Task<PersistentObject> SavePersistentObjectAsync(PersistentObject persistentObject);
    Task DeletePersistentObjectAsync(Guid objectTypeId, string id);
}
