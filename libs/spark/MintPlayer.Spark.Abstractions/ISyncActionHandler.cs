namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Processes incoming sync actions on the owner module by resolving the entity type
/// from the collection name and running the operation through the actions pipeline.
/// Implemented by MintPlayer.Spark; called by the sync endpoint in MintPlayer.Spark.Replication.
/// </summary>
public interface ISyncActionHandler
{
    /// <summary>
    /// Saves (inserts or updates) an entity from a sync action.
    /// Resolves the CLR type from the collection name, deserializes the JSON data,
    /// and runs the save through the IPersistentObjectActions pipeline.
    /// When <paramref name="properties"/> is set, performs a partial merge: loads the
    /// existing entity and only updates the specified properties, preserving owner-only fields.
    /// </summary>
    /// <param name="collection">The RavenDB collection name (e.g., "Cars")</param>
    /// <param name="documentId">The document ID (null for inserts)</param>
    /// <param name="data">The entity data as a dictionary of property names to values</param>
    /// <param name="properties">Property names to update (null for full replacement)</param>
    /// <param name="mustExist">
    /// An Update from a replica (#467, D15): applied only to a row this module still has, at the
    /// version it has now, so an edit of a row deleted here is refused instead of recreating it, and a
    /// write landing meanwhile is a conflict rather than overwritten. An Insert passes false.
    /// </param>
    /// <param name="initiatorId">
    /// The user the replica made the edit for (<c>SyncAction.InitiatorId</c>), exposed to the save's
    /// interceptors through <see cref="Authentication.ISparkSyncInitiator"/> so the owner stamps
    /// <c>ModifiedBy</c> with it (#271, F2). <see langword="null"/> when the replica stated none.
    /// </param>
    /// <returns>The document ID of the saved entity</returns>
    Task<string?> HandleSaveAsync(string collection, string? documentId, Dictionary<string, object?> data, string[]? properties = null, bool mustExist = false, string? initiatorId = null);

    /// <summary>
    /// Deletes an entity from a sync action.
    /// Resolves the CLR type from the collection name and runs the delete
    /// through the IPersistentObjectActions pipeline.
    /// </summary>
    /// <param name="collection">The RavenDB collection name (e.g., "Cars")</param>
    /// <param name="documentId">The document ID to delete</param>
    Task HandleDeleteAsync(string collection, string documentId);
}
