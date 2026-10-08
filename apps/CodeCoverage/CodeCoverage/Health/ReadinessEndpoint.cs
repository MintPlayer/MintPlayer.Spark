using CodeCoverage.Services;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.Health;

/// <summary>
/// <c>GET /health/ready</c>: readiness that can actually fail (#13 U1 / roadmap T0.4). Anonymous.
/// </summary>
/// <remarks>
/// A production contract: the deploy workflow polls it and fails the deploy on 503. It answers 503
/// only when the GitHub App key is decisively unusable (<see cref="GitHubAppReadiness.Failed"/>);
/// skipped, ready and degraded all answer 200. The body is
/// <c>{ "status": "ready" | "unready", "gitHubApp": { "status", "detail" } }</c> either way.
/// </remarks>
internal sealed partial class ReadinessEndpoint : IGetEndpoint
{
    public static string Path => "/health/ready";

    [Inject] private readonly IGitHubAppReadinessService readiness;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var gitHubApp = await readiness.CheckAsync(httpContext.RequestAborted);
        var failed = gitHubApp.Status == GitHubAppReadiness.Failed;
        var payload = new { status = failed ? "unready" : "ready", gitHubApp };
        return failed
            ? Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Json(payload);
    }
}
