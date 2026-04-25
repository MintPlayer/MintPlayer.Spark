using MintPlayer.Spark.Abstractions.ClientOperations;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Builds <see cref="ClientOperationEnvelope"/>-wrapped responses for action
/// endpoints. Endpoints route their success / error / retry paths through
/// <see cref="Envelope"/> so the wire shape is uniform.
/// </summary>
internal static class ClientResult
{
    public static IResult Envelope(IClientAccessor client, object? result, int statusCode)
        => Results.Json(
            new ClientOperationEnvelope
            {
                Result = result,
                Operations = client.Operations,
            },
            statusCode: statusCode);
}
