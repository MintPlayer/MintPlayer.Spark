namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Intercepts write operations on replicated entities and forwards them to the owner module.
/// Implemented by MintPlayer.Spark.Replication; resolved optionally from DI by DatabaseAccess.
/// When not registered (no replication package), write operations proceed locally as normal.
/// </summary>
public interface ISyncActionInterceptor
{
    /// <summary>
    /// Checks whether the given entity type is a replicated entity (has [Replicated] attribute).
    /// </summary>
    bool IsReplicated(Type entityType);

    /// <summary>
    /// Forwards a save (insert or update) operation to the owner module via the message bus.
    /// Uses the PersistentObject with IsValueChanged metadata to determine which properties changed.
    /// Called from the PersistentObject save path (frontend-driven saves).
    /// </summary>
    /// <param name="entityType">The resolved CLR entity type</param>
    /// <param name="obj">The PersistentObject with attribute metadata</param>
    /// <param name="isNew">
    /// Whether the local save created the object. Sent as an Insert or an Update: the owner applies an
    /// Update only to a row it still has, so an edit of a row the owner deleted is refused there rather
    /// than recreating it (#467, D15). Stated by the caller because the object already carries its
    /// generated id by the time it is forwarded.
    /// </param>
    Task HandleSaveAsync(Type entityType, PersistentObject obj, bool isNew);

    /// <summary>
    /// Forwards a save (insert or update) operation to the owner module via the message bus.
    /// Called from the typed entity save path (programmatic saves).
    /// All writable properties are sent since change tracking is not available.
    /// </summary>
    /// <param name="entity">The entity to save</param>
    /// <param name="documentId">The document ID</param>
    /// <param name="isNew">Whether the local save created the document; see the overload above.</param>
    Task HandleSaveAsync(object entity, string? documentId, bool isNew);

    /// <summary>
    /// Forwards a delete operation to the owner module via the message bus.
    /// </summary>
    /// <param name="entityType">The CLR type of the entity</param>
    /// <param name="documentId">The document ID to delete</param>
    Task HandleDeleteAsync(Type entityType, string documentId);
}
