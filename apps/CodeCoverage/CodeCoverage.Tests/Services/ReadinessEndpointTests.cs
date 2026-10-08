using System.Text.Json;
using CodeCoverage.Health;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// <c>GET /health/ready</c> is a production contract: the deploy workflow fails the deploy on 503. Only
/// a decisively unusable GitHub App key (<see cref="GitHubAppReadiness.Failed"/>) answers 503; every
/// other probe result answers 200, and the body names both states either way. The HTTP route itself
/// (anonymous, at its path) is pinned by <c>UploadsHttpTests</c> and the route table snapshot.
/// </summary>
public class ReadinessEndpointTests
{
    private sealed class FixedReadiness(GitHubAppReadinessResult result) : IGitHubAppReadinessService
    {
        public CancellationToken SeenToken { get; private set; }

        public Task<GitHubAppReadinessResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            SeenToken = cancellationToken;
            return Task.FromResult(result);
        }
    }

    private static async Task<(int Status, JsonElement Body, CancellationToken Seen, CancellationToken Aborted)> GetAsync(GitHubAppReadinessResult result)
    {
        var readiness = new FixedReadiness(result);
        using var aborted = new CancellationTokenSource();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            RequestAborted = aborted.Token,
        };
        context.Response.Body = new MemoryStream();

        var response = await new ReadinessEndpoint(readiness).HandleAsync(context);
        await response.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        var body = (await JsonDocument.ParseAsync(context.Response.Body)).RootElement.Clone();
        return (context.Response.StatusCode, body, readiness.SeenToken, aborted.Token);
    }

    [Fact]
    public async Task A_failed_GitHub_App_key_answers_503_unready()
    {
        var (status, body, _, _) = await GetAsync(new GitHubAppReadinessResult(GitHubAppReadiness.Failed, "401"));

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.GetProperty("status").GetString().Should().Be("unready");
        body.GetProperty("gitHubApp").GetProperty("status").GetString().Should().Be("failed");
        body.GetProperty("gitHubApp").GetProperty("detail").GetString().Should().Be("401");
    }

    [Theory]
    [InlineData(GitHubAppReadiness.Skipped)]
    [InlineData(GitHubAppReadiness.Ready)]
    [InlineData(GitHubAppReadiness.Degraded)]
    public async Task Any_other_probe_result_answers_200_ready(string probe)
    {
        var (status, body, _, _) = await GetAsync(new GitHubAppReadinessResult(probe, null));

        status.Should().Be(StatusCodes.Status200OK);
        body.GetProperty("status").GetString().Should().Be("ready");
        body.GetProperty("gitHubApp").GetProperty("status").GetString().Should().Be(probe);
    }

    [Fact]
    public async Task The_probe_is_cancelled_with_the_request()
    {
        var (_, _, seen, aborted) = await GetAsync(new GitHubAppReadinessResult(GitHubAppReadiness.Ready, null));

        seen.Should().Be(aborted);
    }
}
