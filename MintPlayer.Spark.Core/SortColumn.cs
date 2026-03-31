namespace MintPlayer.Spark.Abstractions;

public sealed class SortColumn
{
    public required string Property { get; set; }
    public string Direction { get; set; } = "asc";
}
