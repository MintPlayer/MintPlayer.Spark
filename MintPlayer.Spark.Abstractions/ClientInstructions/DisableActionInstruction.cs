namespace MintPlayer.Spark.Abstractions.ClientInstructions;

/// <summary>
/// Tells the frontend to disable an action button for the duration defined by
/// <see cref="Target"/>.
/// </summary>
public sealed class DisableActionInstruction : ClientInstruction
{
    public required string ActionName { get; init; }
    public required DisableTarget Target { get; init; }
}
