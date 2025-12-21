namespace MintPlayer.Spark.Abstractions;

public sealed class SparkQuery
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public required string ContextProperty { get; set; }
    public string? SortBy { get; set; }
    public string SortDirection { get; set; } = "asc";
}
