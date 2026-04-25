using MintPlayer.Spark.Abstractions.ClientInstructions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Builds <see cref="ClientInstructionEnvelope"/>-wrapped responses for action
/// endpoints. Endpoints route their success / error / retry paths through
/// <see cref="Envelope"/> so the wire shape is uniform.
/// </summary>
internal static class ClientResult
{
    public static IResult Envelope(IClientAccessor client, object? result, int statusCode)
        => Results.Json(
            new ClientInstructionEnvelope
            {
                Result = result,
                Instructions = client.Instructions,
            },
            statusCode: statusCode);
}
