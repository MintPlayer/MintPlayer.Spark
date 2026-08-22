using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

public sealed class SparkQuery
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public TranslatedString? Description { get; set; }

    /// <summary>
    /// Query data source. Two formats supported:
    /// - "Database.PropertyName" — resolves to an IRavenQueryable property on SparkContext
    /// - "Custom.MethodName" — resolves to a method on the entity's Actions class
    /// </summary>
    public required string Source { get; set; }

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
    /// The RavenDB index this query runs against, resolved by name through the index catalog; its
    /// <c>[FromIndex]</c> projection (when it has one) becomes the result shape. Stamped with the
    /// entity's default index when the synchronizer mints the query; a hand-authored value is
    /// preserved and authoritative. Empty falls back to the entity file's declared binding, and an
    /// empty binding queries the raw collection.
    /// </summary>
    public string? IndexName { get; set; }

    /// <summary>
    /// The entity/view-model type name this query returns (e.g., "Person", "CompanyProductsOverview").
    /// Optional. When set, the framework uses the corresponding EntityTypeDefinition from
    /// App_Data/Model/ to map results via IEntityMapper. When not set, the type is inferred:
    /// - For Database queries: from the IRavenQueryable generic parameter
    /// - For Custom queries: from the method return type's generic parameter
    /// </summary>
    public string? EntityType { get; set; }

    /// <summary>
    /// When true, this query supports WebSocket streaming.
    /// The frontend opens a WebSocket to /spark/queries/{id}/stream
    /// and receives snapshot + patch messages instead of a single HTTP response.
    /// </summary>
    public bool IsStreamingQuery { get; set; }

    /// <summary>
    /// A copy of this query with different sort columns, for per-request sort overrides.
    /// </summary>
    /// <remarks>
    /// The definition loaded from the model file is cached and shared across requests, so a
    /// request-scoped override must not mutate it. This exists instead of hand-writing the copy
    /// at the call site: that version silently dropped every field nobody remembered to add to
    /// it (<c>Description</c> was already missing), and each new property here would quietly
    /// vanish from any query that overrode its sort.
    /// </remarks>
    public SparkQuery WithSortColumns(SortColumn[] sortColumns)
    {
        var copy = (SparkQuery)MemberwiseClone();
        copy.SortColumns = sortColumns;
        return copy;
    }
}
