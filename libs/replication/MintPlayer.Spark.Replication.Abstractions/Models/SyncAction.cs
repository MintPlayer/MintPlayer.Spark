using System.Text.Json.Serialization;
using System.Reflection;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Replication.Abstractions.Models;

// Accept both the string form ("Delete") and the numeric form over the wire. The
// /spark/sync/apply endpoint reads the body before the mTLS/module check, so a body
// that fails to bind would 400 ahead of the intended 403 for an unknown module.
[JsonConverter(typeof(JsonStringEnumConverter<SyncActionType>))]
public enum SyncActionType
{
    Insert,
    Update,
    Delete
}

/// <summary>
/// Non-generic transport form used for JSON serialization over HTTP and RavenDB persistence.
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
    /// The entity data as a dictionary of property names to values.
    /// Required for Insert and Update. Null for Delete.
    /// Uses Dictionary instead of JsonElement to ensure compatibility with both
    /// System.Text.Json (HTTP) and Newtonsoft.Json (RavenDB).
    /// </summary>
    public Dictionary<string, object?>? Data { get; set; }

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
    /// by converting the entity data to a property dictionary.
    /// </summary>
    public SyncAction ToTransport() => new()
    {
        ActionType = ActionType,
        Collection = Collection,
        DocumentId = DocumentId,
        Data = Data != null ? EntityToDictionary(Data) : null,
        Properties = Properties,
    };

    /// <summary>
    /// The readable public instance properties of <typeparamref name="T"/> that are part of the Spark
    /// model, computed once per closed type. <c>[IgnoreProperty]</c> ones are excluded from the model, so
    /// excluded from the transport payload too. Plain reflection rather than Abstractions' accessor
    /// cache, so this package does not depend on the Web-SDK Abstractions (#388).
    /// </summary>
    private static readonly PropertyInfo[] TransportProperties = typeof(T)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<IgnorePropertyAttribute>() is null)
        .ToArray();

    private static Dictionary<string, object?> EntityToDictionary(T entity)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in TransportProperties)
            dict[prop.Name] = prop.GetValue(entity);
        return dict;
    }
}
