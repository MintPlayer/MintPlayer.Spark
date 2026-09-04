using System.Security.Claims;
using CodeCoverage.ApiTokens;
using CodeCoverage.Controllers;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>The status response carries the commit's assembly next to the build's own numbers.</summary>
public class UploadsControllerAssemblyStatusTests : CoverageRavenTest
{
    private const long RepoId = 4343;
    private const string RepoName = "acme/gadgets";
    private const string Sha = "3333333333333333333333333333333333333333";

    private sealed class NullMessageBus : IMessageBus
    {
        public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task BroadcastAsync<TMessage>(TMessage message, string queueName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, string queueName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static UploadsController CreateController(IAsyncDocumentSession session)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(session);
        services.AddSingleton<IMessageBus>(new NullMessageBus());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Coverage:BaseUrl"] = "https://coverage.example.com" })
            .Build());
        services.AddSingleton<IGitHubDiffService>(new Services.ScriptedDiffService());
        services.AddScoped<IBaseResolver, BaseResolver>();
        services.AddScoped<UploadsController>();

        var controller = services.BuildServiceProvider().GetRequiredService<UploadsController>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ApiTokenAuthenticationHandler.ScopeClaim, "Account"),
                    new Claim(ApiTokenAuthenticationHandler.AccountClaim, "acme"),
                ], ApiTokenAuthenticationHandler.SchemeName)),
            },
        };
        return controller;
    }

    private static UploadsController.UploadStatusResponse Body(ActionResult<UploadsController.UploadStatusResponse> result)
        => (UploadsController.UploadStatusResponse)((OkObjectResult)result.Result!).Value!;

    [Fact]
    public async Task Status_reports_the_commit_assembly_beside_the_builds_own_coverage()
    {
        using var store = GetDocumentStore();
        var commitId = Commit.DocumentId(RepoId, Sha);
        var buildId = Build.DocumentId(RepoId, Sha, 7, 1);

        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository
            {
                GitHubId = RepoId, Name = "gadgets", FullName = RepoName, OwnerLogin = "acme", IsPrivate = true, DefaultBranch = "master",
            }, Repository.DocumentId(RepoId));
            await seed.StoreAsync(new Commit
            {
                Sha = Sha, Repository = Repository.DocumentId(RepoId), Branch = "feature", FirstSeenAtUtc = DateTimeOffset.UtcNow,
                Coverage = new CoverageSummary { LinesCovered = 50, LinesCoverable = 100, FilesCount = 10 },
                AssemblyCompleteness = CommitAssembly.Complete,
            }, commitId);
            await seed.StoreAsync(new Build
            {
                Commit = commitId, CiRunId = 7, CiRunAttempt = 1, Status = "Finalized", FinalizeReason = "Explicit",
                CreatedAtUtc = DateTime.UtcNow, FinalizedAtUtc = DateTime.UtcNow,
                Coverage = new CoverageSummary { LinesCovered = 5, LinesCoverable = 10, FilesCount = 1 },
                Sessions = [new BuildSession { SessionId = "s1", ParseStatus = "Parsed", FilesCount = 1 }],
            }, buildId);
            await seed.StoreAsync(new CommitAssembly
            {
                Commit = commitId, Sha = Sha,
                Builds = [new AssemblyBuild { BuildId = buildId, CiRunId = 7, CiRunAttempt = 1, Partial = true }],
                BaseSha = "base", BaseResolution = ResolvedBase.Exact,
                MeasuredFiles = 1, CarriedFiles = 9, UnmeasuredFiles = 0,
                Coverage = new CoverageSummary { LinesCovered = 50, LinesCoverable = 100, FilesCount = 10 },
                Completeness = CommitAssembly.Complete, OldestOriginSha = "older", AssembledAtUtc = DateTime.UtcNow,
            }, CommitAssembly.DocumentId(commitId));
            await seed.SaveChangesAsync();
        }
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var response = Body(await CreateController(session).Status(RepoName, Sha, 7));

        // The build's own number is untouched…
        response.Coverage!.LinesCoverable.Should().Be(10);
        // …and the commit's assembled record rides along.
        response.Assembly.Should().NotBeNull();
        response.Assembly!.Coverage.LinesCoverable.Should().Be(100);
        response.Assembly.Completeness.Should().Be(CommitAssembly.Complete);
        response.Assembly.CarriedFiles.Should().Be(9);
        response.Assembly.BaseSha.Should().Be("base");
        response.Assembly.OldestOriginSha.Should().Be("older");
        response.Assembly.Builds.Should().Equal([buildId]);
    }

    [Fact]
    public async Task Status_has_no_assembly_before_the_first_finalize()
    {
        using var store = GetDocumentStore();
        var commitId = Commit.DocumentId(RepoId, Sha);

        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository
            {
                GitHubId = RepoId, Name = "gadgets", FullName = RepoName, OwnerLogin = "acme", IsPrivate = true, DefaultBranch = "master",
            }, Repository.DocumentId(RepoId));
            await seed.StoreAsync(new Commit { Sha = Sha, Repository = Repository.DocumentId(RepoId), FirstSeenAtUtc = DateTimeOffset.UtcNow }, commitId);
            await seed.StoreAsync(new Build
            {
                Commit = commitId, CiRunId = 7, CiRunAttempt = 1, Status = "Open", CreatedAtUtc = DateTime.UtcNow,
                Sessions = [new BuildSession { SessionId = "s1", ParseStatus = "Pending" }],
            }, Build.DocumentId(RepoId, Sha, 7, 1));
            await seed.SaveChangesAsync();
        }
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var response = Body(await CreateController(session).Status(RepoName, Sha, 7));

        response.Assembly.Should().BeNull();
    }
}
