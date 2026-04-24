namespace MintPlayer.Spark.Abstractions.ClientInstructions;

/// <summary>
/// Patches a single attribute on a currently-open PersistentObject on the frontend.
/// If the target PO is not displayed, the instruction is silently dropped by the
/// frontend dispatcher (no error).
/// </summary>
/// <remarks>
/// <see cref="Value"/> is the new attribute value as produced by the server.
/// Sending <see cref="Value"/> inline avoids a round-trip fetch when the server
/// already computed the new value. Future extension: a null <see cref="Value"/>
/// with a protocol signal could mean "refetch" rather than "set to null" — not
/// specified yet.
/// </remarks>
public sealed class RefreshAttributeInstruction : ClientInstruction
{
    public required Guid ObjectTypeId { get; init; }
    public required string Id { get; init; }
    public required string AttributeName { get; init; }
    public object? Value { get; init; }
}
