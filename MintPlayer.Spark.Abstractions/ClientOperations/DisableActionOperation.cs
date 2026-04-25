namespace MintPlayer.Spark.Abstractions.ClientOperations;

/// <summary>
/// Tells the frontend to disable an action button for the duration defined by
/// <see cref="Target"/>.
/// </summary>
public sealed class DisableActionOperation : ClientOperation
{
    public required string ActionName { get; init; }
    public required DisableTarget Target { get; init; }
}
