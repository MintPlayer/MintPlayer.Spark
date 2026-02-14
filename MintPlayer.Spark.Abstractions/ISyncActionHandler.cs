using System.Text.Json;

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
    /// <param name="data">The entity data as a JSON element</param>
    /// <param name="properties">Property names to update (null for full replacement)</param>
    /// <returns>The document ID of the saved entity</returns>
    Task<string?> HandleSaveAsync(string collection, string? documentId, JsonElement data, string[]? properties = null);

    /// <summary>
    /// Deletes an entity from a sync action.
    /// Resolves the CLR type from the collection name and runs the delete
    /// through the IPersistentObjectActions pipeline.
    /// </summary>
    /// <param name="collection">The RavenDB collection name (e.g., "Cars")</param>
    /// <param name="documentId">The document ID to delete</param>
    Task HandleDeleteAsync(string collection, string documentId);
}
