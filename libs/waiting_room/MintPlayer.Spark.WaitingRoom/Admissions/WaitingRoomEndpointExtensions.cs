using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.Spark.WaitingRoom;

/// <summary>Guards a host route with a waiting room's admission token (§4.12: "hold and checkout APIs refuse requests without it").</summary>
public static class WaitingRoomEndpointExtensions
{
    /// <summary>The request header a client sends the admission token in.</summary>
    public const string AdmissionHeader = "X-Spark-Admission";

    /// <summary>
    /// Refuses the endpoint (403 <c>{ "admission": "&lt;status&gt;" }</c>) while a room gates the phase
    /// <paramref name="phase"/> returns and the request carries no valid token in <see cref="AdmissionHeader"/>. With
    /// <paramref name="consumer"/>, the token is spent for that consumer (single use); without, it is only validated.
    /// A phase no room gates passes through.
    /// </summary>
    public static TBuilder RequireWaitingRoomAdmission<TBuilder>(
        this TBuilder builder, Func<HttpContext, string?> phase, Func<HttpContext, string?>? consumer = null)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(phase);
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (phase(http) is not { Length: > 0 } gate)
                return await next(context);
            var admissions = http.RequestServices.GetRequiredService<IWaitingRoomAdmissions>();
            if (!await admissions.IsGatedAsync(gate, http.RequestAborted))
                return await next(context);
            var token = http.Request.Headers[AdmissionHeader].ToString();
            var check = consumer?.Invoke(http) is { Length: > 0 } spender
                ? await admissions.ConsumeAsync(token, gate, spender, cancellationToken: http.RequestAborted)
                : await admissions.ValidateAsync(token, gate, cancellationToken: http.RequestAborted);
            return check.IsValid
                ? await next(context)
                : Results.Json(new { admission = check.Status.ToString() }, statusCode: StatusCodes.Status403Forbidden);
        });
        return builder;
    }
}
