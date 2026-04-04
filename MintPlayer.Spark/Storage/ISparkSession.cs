namespace MintPlayer.Spark.Storage;

/// <summary>
/// Storage-agnostic session for querying, loading, storing, and deleting entities.
/// Replaces direct dependency on database-specific session types.
/// </summary>
public interface ISparkSession : IDisposable
{
    /// <summary>
    /// Creates a queryable for the specified entity type.
    /// </summary>
    IQueryable<T> Query<T>() where T : class;

    /// <summary>
    /// Creates a queryable for the specified entity type, optionally using a named index.
    /// </summary>
    /// <param name="indexName">Optional index name. Provider-specific interpretation (e.g., RavenDB index name).</param>
    IQueryable<T> Query<T>(string? indexName) where T : class;

    /// <summary>
    /// Creates a queryable for the specified entity type using an index type.
    /// In RavenDB this maps to session.Query&lt;T, TIndexCreator&gt;() which returns stored/computed index fields.
    /// </summary>
    IQueryable<T> Query<T>(Type indexType) where T : class;

    /// <summary>
    /// Loads a single entity by its ID.
    /// </summary>
    Task<T?> LoadAsync<T>(string id) where T : class;

    /// <summary>
    /// Stores (creates or updates) an entity in the session.
    /// Changes are persisted when <see cref="SaveChangesAsync"/> is called.
    /// </summary>
    Task StoreAsync<T>(T entity) where T : class;

    /// <summary>
    /// Persists all pending changes in the session to the storage backend.
    /// </summary>
    Task SaveChangesAsync();

    /// <summary>
    /// Marks a document for deletion by its ID.
    /// Deletion is persisted when <see cref="SaveChangesAsync"/> is called.
    /// </summary>
    void Delete(string id);

    /// <summary>
    /// Marks an entity for deletion.
    /// Deletion is persisted when <see cref="SaveChangesAsync"/> is called.
    /// </summary>
    void Delete<T>(T entity) where T : class;
}
