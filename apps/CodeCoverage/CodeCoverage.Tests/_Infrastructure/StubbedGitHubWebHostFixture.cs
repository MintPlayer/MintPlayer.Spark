using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests._Infrastructure;

/// <summary>
/// The real application with GitHub replaced by <see cref="StubGitHub"/>, for HTTP tests whose
/// request reaches a forge call — a fork upload reads its pull request back from GitHub.
/// </summary>
/// <remarks>
/// Only the installation service is swapped; everything above it (<c>GitHubForgeClient</c>, the
/// integration, the resolver) is the app's own registration, so the request travels the same path
/// it does in production and stops only where it would leave the process. Shared through
/// <see cref="CoverageWebHostCollection"/>; see there for why it must not boot alongside another host.
/// </remarks>
public sealed class StubbedGitHubWebHostFixture : CoverageRavenTest, IAsyncLifetime
{
    public IDocumentStore Store { get; private set; } = null!;
    public CoverageWebAppFactory Factory { get; private set; } = null!;
    internal StubGitHub GitHub { get; } = new();

    public void WaitForIndexing() => WaitForIndexing(Store);

    public Task InitializeAsync()
    {
        Store = GetDocumentStore();
        Factory = new CoverageWebAppFactory(Store, services =>
        {
            services.RemoveAll<IGitHubInstallationService>();
            services.AddSingleton<IGitHubInstallationService>(GitHub);
        });

        // Boot here, so a startup failure is attributed to the fixture (see CoverageWebHostFixture).
        _ = Factory.CreateClient();
        return Task.CompletedTask;
    }

    /// <summary>The same three independent, non-throwing teardown steps as <see cref="CoverageWebHostFixture"/>.</summary>
    public new async Task DisposeAsync()
    {
        try { await Factory.DisposeAsync(); }
        catch (Exception ex) { Console.WriteLine($"[StubbedGitHubWebHostFixture] host disposal faulted: {ex}"); }

        try { Store?.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[StubbedGitHubWebHostFixture] store disposal faulted: {ex}"); }

        try { base.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[StubbedGitHubWebHostFixture] RavenDB test-driver disposal faulted: {ex}"); }
    }
}
