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
    /// <summary>
    /// Refuse the request in the envelope shape, using the uniform denial contract.
    /// </summary>
    /// <remarks>
    /// See <c>SparkDenial</c> for why denied, unknown-type and not-found are deliberately
    /// indistinguishable (security-audit M-3). Every envelope endpoint must refuse through
    /// here rather than hand-rolling a status, or the shapes drift apart and the oracle
    /// comes back.
    /// </remarks>
    public static IResult EnvelopeRefusal(IClientAccessor client, HttpContext httpContext)
    {
        var (body, statusCode) = Endpoints.SparkDenial.Refuse(httpContext);
        return Envelope(client, body, statusCode);
    }

    /// <summary>
    /// A submitted action the actions class disabled (#460, D13): <c>403</c>, naming the action, so a
    /// client can tell it apart from a missing row (404), a malformed request (400) and a throttle (429).
    /// </summary>
    public static IResult ActionDisabled(IClientAccessor client, SparkActionDisabledException ex)
        => Envelope(client, new { error = ex.Message, action = ex.ActionName }, StatusCodes.Status403Forbidden);

    /// <summary>
    /// A write refused by a business quota (#460, M12): <c>429</c> in the envelope, with a
    /// <c>Retry-After</c> header (whole seconds, rounded up) when the exception knows it.
    /// </summary>
    public static IResult Throttled(IClientAccessor client, HttpContext httpContext, Abstractions.SparkThrottledException ex)
    {
        int? seconds = ex.RetryAfter is { } after ? (int)Math.Ceiling(Math.Max(0, after.TotalSeconds)) : null;
        if (seconds is { } s)
            httpContext.Response.Headers.RetryAfter = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Envelope(client, new { error = ex.Message, retryAfterSeconds = seconds }, StatusCodes.Status429TooManyRequests);
    }

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
