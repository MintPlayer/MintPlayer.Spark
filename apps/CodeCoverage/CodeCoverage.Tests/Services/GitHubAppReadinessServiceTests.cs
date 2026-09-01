using CodeCoverage.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// #13 U1: the readiness probe's tri-state mapping. "failed" needs decisive
/// evidence the key is unusable — a GitHub outage (or any inconclusive error)
/// must stay "degraded" so it can't take the container's readiness down, and
/// the unconfigured dev state must never fail at all.
/// </summary>
public class GitHubAppReadinessServiceTests
{
    private sealed class StubEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "CodeCoverage.Tests";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IGitHubAppReadinessService CreateService(Dictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.AddSingleton<IWebHostEnvironment>(new StubEnvironment());
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<GitHubAppReadinessService>>(NullLogger<GitHubAppReadinessService>.Instance);
        services.AddSingleton<IGitHubAppReadinessService, GitHubAppReadinessService>();
        return services.BuildServiceProvider().GetRequiredService<IGitHubAppReadinessService>();
    }

    [Fact]
    public async Task An_unconfigured_app_is_skipped_not_failed()
    {
        var result = await CreateService([]).CheckAsync();
        result.Status.Should().Be(GitHubAppReadiness.Skipped);
    }

    [Fact]
    public async Task A_key_path_without_a_parseable_app_id_is_still_skipped()
    {
        var result = await CreateService(new Dictionary<string, string?>
        {
            ["GitHub:Production:PrivateKeyPath"] = "/run/secrets/github-app.pem",
            ["GitHub:Production:AppId"] = "not-a-number",
        }).CheckAsync();
        result.Status.Should().Be(GitHubAppReadiness.Skipped);
    }

    [Fact]
    public void A_rejected_jwt_is_decisively_failed()
        => GitHubAppReadinessService.Classify(new AuthorizationException())
            .Status.Should().Be(GitHubAppReadiness.Failed);

    [Fact]
    public void An_unreadable_or_malformed_key_is_decisively_failed()
    {
        GitHubAppReadinessService.Classify(new FileNotFoundException("no pem"))
            .Status.Should().Be(GitHubAppReadiness.Failed, "FileNotFoundException is an IOException");
        GitHubAppReadinessService.Classify(new UnauthorizedAccessException("pem not readable by UID 1654"))
            .Status.Should().Be(GitHubAppReadiness.Failed);
        GitHubAppReadinessService.Classify(new System.Security.Cryptography.CryptographicException("not a key"))
            .Status.Should().Be(GitHubAppReadiness.Failed);
    }

    [Fact]
    public void Anything_inconclusive_is_degraded_never_failed()
    {
        GitHubAppReadinessService.Classify(new HttpRequestException("connection refused"))
            .Status.Should().Be(GitHubAppReadiness.Degraded);
        GitHubAppReadinessService.Classify(new TaskCanceledException("timeout"))
            .Status.Should().Be(GitHubAppReadiness.Degraded);
        GitHubAppReadinessService.Classify(new ApiException())
            .Status.Should().Be(GitHubAppReadiness.Degraded, "a 5xx from GitHub is GitHub's bad minute, not our key");
    }
}
