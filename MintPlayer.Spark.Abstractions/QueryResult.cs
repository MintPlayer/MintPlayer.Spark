namespace MintPlayer.Spark.Abstractions;

public sealed class QueryResult
{
    public required IEnumerable<PersistentObject> Data { get; set; }
    public required int TotalRecords { get; set; }
    public required int Skip { get; set; }
    public required int Take { get; set; }
}
