using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

public class SparkQuery
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public TranslatedString? Description { get; set; }

    /// <summary>
    /// Query data source. Two formats supported:
    /// - "Database.PropertyName" — resolves to an IRavenQueryable property on SparkContext
    /// - "Custom.MethodName" — resolves to a method on the entity's Actions class
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Optional URL-friendly alias for this query.
    /// Used as an alternative to the GUID in URLs (e.g., /query/cars instead of /query/{guid}).
    /// If not set, auto-generated from Name by stripping "Get" prefix and lowercasing.
    /// </summary>
    public string? Alias { get; set; }

    /// <summary>
    /// Multi-column sort specification.
    /// Each entry specifies a property name and direction ("asc"/"desc").
    /// Applied in order: first entry = primary sort, subsequent = tiebreakers.
    /// </summary>
    public SortColumn[] SortColumns { get; set; } = [];

    /// <summary>
    /// Controls how query results are rendered in the UI.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SparkQueryRenderMode RenderMode { get; set; } = SparkQueryRenderMode.Pagination;

    /// <summary>
    /// Optional RavenDB index name. When specified, the query will use this index
    /// and return the projection type registered in the IndexRegistry.
    /// </summary>
    public string? IndexName { get; set; }

    /// <summary>
    /// When true, indicates this query uses a projection type (from IndexRegistry)
    /// rather than the full entity type.
    /// </summary>
    public bool UseProjection { get; set; }

    /// <summary>
    /// The entity/view-model type name this query returns (e.g., "Person", "CompanyProductsOverview").
    /// Optional. When set, the framework uses the corresponding EntityTypeDefinition from
    /// App_Data/Model/ to map results via IEntityMapper. When not set, the type is inferred:
    /// - For Database queries: from the IRavenQueryable generic parameter
    /// - For Custom queries: from the method return type's generic parameter
    /// </summary>
    [Reference(typeof(EntityTypeDefinition), "GetPersistentObjectDefinitions")]
    public string? EntityType { get; set; }

    /// <summary>
    /// When true, this query supports WebSocket streaming.
    /// The frontend opens a WebSocket to /spark/queries/{id}/stream
    /// and receives snapshot + patch messages instead of a single HTTP response.
    /// </summary>
    public bool IsStreamingQuery { get; set; }

    /// <summary>
    /// Optional back-reference to the parent entity type's name.
    /// Set by loaders that flatten the hierarchy (e.g., SparkEditor).
    /// Ignored during normal framework operation.
    /// </summary>
    public string? PersistentObjectName { get; set; }
}
