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
    public string? SortBy { get; set; }
    public string SortDirection { get; set; } = "asc";

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
    public string? EntityType { get; set; }
}
