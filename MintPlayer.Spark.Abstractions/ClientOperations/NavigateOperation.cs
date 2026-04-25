namespace MintPlayer.Spark.Abstractions.ClientOperations;

/// <summary>
/// Tells the frontend to navigate. Exactly one of
/// (<see cref="ObjectTypeId"/> + <see cref="Id"/>) or <see cref="RouteName"/> is set.
/// </summary>
public sealed class NavigateOperation : ClientOperation
{
    public Guid? ObjectTypeId { get; init; }
    public string? Id { get; init; }
    public string? RouteName { get; init; }
}
