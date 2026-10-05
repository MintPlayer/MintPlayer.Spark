namespace MintPlayer.Spark.Abstractions.ClientOperations;

/// <summary>
/// Tells the frontend to re-execute a query wherever it's currently displayed.
/// Silently dropped when the query is not open.
/// </summary>
public sealed class RefreshQueryOperation : ClientOperation
{
    /// <summary>The query's id or alias, case-insensitive, sent as given.</summary>
    public required string QueryId { get; init; }
}
