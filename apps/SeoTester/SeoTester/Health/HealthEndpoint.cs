using MintPlayer.AspNetCore.Endpoints;

namespace SeoTester.Health;

/// <summary>
/// <c>GET /health</c>: liveness, always 200 with an empty body. Anonymous. As CodeCoverage's.
/// </summary>
/// <remarks>
/// A production contract: the compose healthcheck (<c>apps/SeoTester/docker-compose.yml</c>) and the deploy's readiness
/// loop probe it, so it depends on nothing that can be misconfigured (database, credentials, the browser): a bad
/// secret must not restart-loop the container. The process answering is what it proves.
/// </remarks>
internal sealed partial class HealthEndpoint : IGetEndpoint
{
    public static string Path => "/health";

    public Task<IResult> HandleAsync(HttpContext httpContext) => Task.FromResult(Results.Ok());
}
