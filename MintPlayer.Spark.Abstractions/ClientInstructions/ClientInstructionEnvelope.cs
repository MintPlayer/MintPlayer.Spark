namespace MintPlayer.Spark.Abstractions.ClientInstructions;

/// <summary>
/// Wire envelope for every action-endpoint response. <see cref="Result"/> holds the
/// primary payload (PO, query result, etc.; null when the action returns no body);
/// <see cref="Instructions"/> holds the accumulated side-effects for the frontend
/// to dispatch.
/// </summary>
public sealed class ClientInstructionEnvelope
{
    public object? Result { get; init; }
    public required IReadOnlyList<ClientInstruction> Instructions { get; init; }
}
