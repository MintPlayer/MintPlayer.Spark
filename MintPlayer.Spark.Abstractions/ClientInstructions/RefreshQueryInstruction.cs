namespace MintPlayer.Spark.Abstractions.ClientInstructions;

/// <summary>
/// Tells the frontend to re-execute a named query if it's currently displayed.
/// Silently dropped when the query is not open.
/// </summary>
public sealed class RefreshQueryInstruction : ClientInstruction
{
    public required string QueryId { get; init; }
}
