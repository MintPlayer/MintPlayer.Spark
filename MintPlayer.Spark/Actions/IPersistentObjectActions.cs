using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Actions;

/// <summary>
/// Interface defining lifecycle hooks for entity-specific business logic.
/// Implement this interface to customize CRUD behavior for specific entity types.
/// </summary>
/// <typeparam name="T">The entity type</typeparam>
public interface IPersistentObjectActions<T> where T : class
{
    /// <summary>
    /// Called when querying all entities of this type.
    /// </summary>
    /// <param name="session">The RavenDB async document session</param>
    /// <returns>Collection of entities</returns>
    Task<IEnumerable<T>> OnQueryAsync(IAsyncDocumentSession session);

    /// <summary>
    /// Called when loading a single entity by ID.
    /// </summary>
    /// <param name="session">The RavenDB async document session</param>
    /// <param name="id">The document ID</param>
    /// <returns>The entity or null if not found</returns>
    Task<T?> OnLoadAsync(IAsyncDocumentSession session, string id);

    /// <summary>
    /// Called when saving (creating or updating) an entity.
    /// This method should call OnBeforeSaveAsync and OnAfterSaveAsync.
    /// </summary>
    /// <param name="session">The RavenDB async document session</param>
    /// <param name="entity">The entity to save</param>
    /// <returns>The saved entity</returns>
    Task<T> OnSaveAsync(IAsyncDocumentSession session, T entity);

    /// <summary>
    /// Called when deleting an entity.
    /// This method should call OnBeforeDeleteAsync.
    /// </summary>
    /// <param name="session">The RavenDB async document session</param>
    /// <param name="id">The document ID to delete</param>
    Task OnDeleteAsync(IAsyncDocumentSession session, string id);

    /// <summary>
    /// Lifecycle hook called before saving an entity.
    /// Use this to validate, transform, or enrich the entity before persistence.
    /// </summary>
    /// <param name="entity">The entity about to be saved</param>
    Task OnBeforeSaveAsync(T entity);

    /// <summary>
    /// Lifecycle hook called after saving an entity.
    /// Use this for post-save operations like notifications, auditing, or cache invalidation.
    /// </summary>
    /// <param name="entity">The entity that was saved</param>
    Task OnAfterSaveAsync(T entity);

    /// <summary>
    /// Lifecycle hook called before deleting an entity.
    /// Use this for validation, cleanup, or cascade operations.
    /// </summary>
    /// <param name="entity">The entity about to be deleted</param>
    Task OnBeforeDeleteAsync(T entity);
}
