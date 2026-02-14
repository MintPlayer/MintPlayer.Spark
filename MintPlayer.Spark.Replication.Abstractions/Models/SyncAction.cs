using System.Text.Json;

namespace MintPlayer.Spark.Replication.Abstractions.Models;

public enum SyncActionType
{
    Insert,
    Update,
    Delete
}

/// <summary>
/// Non-generic transport form used for JSON serialization over HTTP and the message bus.
/// </summary>
public class SyncAction
{
    /// <summary>The type of operation to perform on the owner module.</summary>
    public required SyncActionType ActionType { get; set; }

    /// <summary>The RavenDB collection name (e.g., "Cars").</summary>
    public required string Collection { get; set; }

    /// <summary>
    /// The document ID. Required for Update and Delete.
    /// For Insert, can be null (owner generates the ID).
    /// </summary>
    public string? DocumentId { get; set; }

    /// <summary>
    /// The entity data as a JSON element. Required for Insert and Update.
    /// Null for Delete.
    /// </summary>
    public JsonElement? Data { get; set; }

    /// <summary>
    /// Property names to update on the owner entity. When set, only these properties
    /// are merged onto the existing entity (partial update). When null, all properties
    /// from the data are applied.
    /// Automatically populated from the replicated entity type's property names,
    /// ensuring that only replicated fields are synced back to the owner.
    /// </summary>
    public string[]? Properties { get; set; }
}

/// <summary>
/// Strongly-typed sync action for type-safe construction.
/// Call <see cref="ToTransport"/> to convert to the non-generic transport form.
/// </summary>
/// <typeparam name="T">The entity type</typeparam>
public class SyncAction<T> where T : class
{
    /// <summary>The type of operation to perform on the owner module.</summary>
    public required SyncActionType ActionType { get; set; }

    /// <summary>The RavenDB collection name (e.g., "Cars").</summary>
    public required string Collection { get; set; }

    /// <summary>
    /// The document ID. Required for Update and Delete.
    /// For Insert, can be null (owner generates the ID).
    /// </summary>
    public string? DocumentId { get; set; }

    /// <summary>
    /// The strongly-typed entity data. Required for Insert and Update.
    /// </summary>
    public T? Data { get; set; }

    /// <summary>
    /// Property names to update on the owner entity. When set, only these properties
    /// are merged onto the existing entity (partial update). When null, all properties
    /// from the data are applied.
    /// </summary>
    public string[]? Properties { get; set; }

    /// <summary>
    /// Converts this strongly-typed sync action to the non-generic transport form
    /// by serializing the data to a <see cref="JsonElement"/>.
    /// </summary>
    public SyncAction ToTransport() => new()
    {
        ActionType = ActionType,
        Collection = Collection,
        DocumentId = DocumentId,
        Data = Data != null ? JsonSerializer.SerializeToElement(Data) : null,
        Properties = Properties,
    };
}
