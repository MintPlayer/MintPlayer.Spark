namespace MintPlayer.Spark.Abstractions.ClientOperations;

/// <summary>
/// Tells the frontend to re-execute a named query if it's currently displayed.
/// Silently dropped when the query is not open.
/// </summary>
public sealed class RefreshQueryOperation : ClientOperation
{
    public required string QueryId { get; init; }
}
