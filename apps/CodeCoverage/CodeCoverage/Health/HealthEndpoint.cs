using MintPlayer.AspNetCore.Endpoints;

namespace CodeCoverage.Health;

/// <summary>
/// <c>GET /health</c>: liveness, always 200 with an empty body. Anonymous.
/// </summary>
/// <remarks>
/// A production contract: the docker compose healthcheck probes it, so it must never depend on the
/// GitHub App key or anything else that can be misconfigured (a bad key must not restart-loop the
/// container). Readiness that can fail is <see cref="ReadinessEndpoint"/>.
/// </remarks>
internal sealed partial class HealthEndpoint : IGetEndpoint
{
    public static string Path => "/health";

    public Task<IResult> HandleAsync(HttpContext httpContext) => Task.FromResult(Results.Ok());
}
