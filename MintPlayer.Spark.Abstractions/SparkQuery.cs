namespace MintPlayer.Spark.Abstractions;

public sealed class SparkQuery
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public required string ContextProperty { get; set; }
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
}
