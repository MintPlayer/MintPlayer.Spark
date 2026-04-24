namespace MintPlayer.Spark.Abstractions.ClientInstructions;

/// <summary>
/// Tells the frontend to navigate. Exactly one of
/// (<see cref="ObjectTypeId"/> + <see cref="Id"/>) or <see cref="RouteName"/> is set.
/// </summary>
public sealed class NavigateInstruction : ClientInstruction
{
    public Guid? ObjectTypeId { get; init; }
    public string? Id { get; init; }
    public string? RouteName { get; init; }
}
