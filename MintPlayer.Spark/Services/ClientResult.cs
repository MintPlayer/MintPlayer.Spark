using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Exceptions;

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

    /// <summary>
    /// 449 retry envelope. Production flow pushes the retry operation onto the accessor
    /// via <c>RetryAccessor.Action()</c> before unwinding, so the operation is already in
    /// the envelope when this method runs. Falls back to building from the exception's
    /// fields when not — covers user code that throws <see cref="SparkRetryActionException"/>
    /// directly without going through <c>IRetryAccessor</c>.
    /// </summary>
    public static IResult Retry(IClientAccessor client, SparkRetryActionException ex)
    {
        if (!client.Operations.Any(o => o is RetryOperation))
        {
            ((ClientAccessor)client).PushRetry(
                ex.Step, ex.Title, ex.Options, ex.DefaultOption, ex.PersistentObject, ex.RetryMessage);
        }
        return Envelope(client, null, 449);
    }
}
