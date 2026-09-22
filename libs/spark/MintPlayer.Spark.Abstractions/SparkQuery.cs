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
    /// Per-column capability overrides for this query only — a <b>sparse</b> list, never a column
    /// enumeration.
    /// </summary>
    /// <remarks>
    /// A column not named here inherits the attribute's own <c>canSort</c>/<c>canFilter</c>/
    /// <c>canListDistincts</c>; an absent or empty list overrides nothing. Sparseness is what keeps
    /// this from becoming a second place that decides which columns exist and in what order — that
    /// remains <c>showedOn</c> plus <c>order</c> on the attributes.
    /// <para>
    /// An entry naming an attribute that is not on this query's surface is a
    /// <c>--spark-verify-model</c> error rather than a silent no-op.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// ⚠️ <b>Nullable, not an empty array.</b> The synchronizer writes with
    /// <c>JsonIgnoreCondition.WhenWritingNull</c>, which skips <see langword="null"/> but <b>not</b> an
    /// empty collection — so a non-nullable default stamps <c>"columns": []</c> onto every query in
    /// every model file. Measured: it did, across all four apps, before this was made nullable.
    /// </remarks>
    public SparkQueryColumn[]? Columns { get; set; }

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
        copy.SortColumnsAreCallerSupplied = true;
        return copy;
    }

    /// <summary>
    /// Whether <see cref="SortColumns"/> came from the request rather than the model.
    /// </summary>
    /// <remarks>
    /// The <c>canSort</c> gate (#431) exempts a column the query itself declares its default order
    /// by — the server chose that ordering, the caller did not, so <c>canSort: false</c> on such a
    /// column means "the grid arrives sorted this way and you may not re-sort by it", which is a
    /// coherent and useful shape.
    /// <para>
    /// Without this flag the distinction is unrecoverable at the point it is needed:
    /// <see cref="WithSortColumns"/> <em>replaces</em> the declared columns, so by the time the
    /// executor sees them a caller-supplied sort is indistinguishable from a model-declared one.
    /// </para>
    /// <para>
    /// <see cref="JsonIgnoreAttribute"/> because it is request state, not model state: it must never
    /// be written to a model file nor accepted from one.
    /// </para>
    /// </remarks>
    [JsonIgnore]
    public bool SortColumnsAreCallerSupplied { get; private set; }
}

/// <summary>
/// One entry in a query's sparse <see cref="SparkQuery.Columns"/> override list.
/// </summary>
/// <remarks>
/// Every flag is nullable and null means "inherit from the attribute" — which is why this cannot be
/// collapsed into <c>bool</c>s with defaults: "not stated here" and "stated false here" are
/// different answers, and only the first defers to the attribute.
/// </remarks>
public sealed class SparkQueryColumn
{
    /// <summary>The attribute name this entry overrides. Must be on the query's surface.</summary>
    public required string Name { get; set; }

    /// <inheritdoc cref="EntityAttributeDefinition.CanSort"/>
    public bool? CanSort { get; set; }

    /// <inheritdoc cref="EntityAttributeDefinition.CanFilter"/>
    public bool? CanFilter { get; set; }

    /// <inheritdoc cref="EntityAttributeDefinition.CanListDistincts"/>
    public bool? CanListDistincts { get; set; }
}
