using CodeCoverage.Controllers;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using CodeCoverage.Tests.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// Once a commit has an assembly, the browse endpoints read it instead of the
/// latest build: the tree carries provenance, and a file measured on another
/// commit is served from the assembled copy.
/// </summary>
public class BrowseControllerAssemblyTests : CoverageRavenTest
{
    private const long RepoId = 3;
    private const string Sha = "bb11284939350991803acc84ced894ade844b9f0";

    private sealed class NullContentService : IGitHubContentService
    {
        public Task<string?> GetFileContentAsync(Repository repository, long? installationId, string sha, string path, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private static BrowseController CreateController(IAsyncDocumentSession session)
    {
        var services = new ServiceCollection();
        services.AddSingleton(session);
        services.AddSingleton<IGitHubAccessService>(new ScriptedAccessService(new([], GitHubTokenState.Ok)));
        services.AddSingleton<IGitHubContentService>(new NullContentService());
        services.AddSingleton(GitHubAuthTestFakes.TestConfiguration());
        services.AddScoped<BrowseController>();
        return services.BuildServiceProvider().GetRequiredService<BrowseController>();
    }

    [Fact]
    public async Task Tree_and_file_come_from_the_assembly_when_one_exists()
    {
        using var store = GetDocumentStore();
        var commitId = Commit.DocumentId(RepoId, Sha);
        var buildId = Build.DocumentId(RepoId, Sha, runId: 7, runAttempt: 1);
        var assemblyId = CommitAssembly.DocumentId(commitId);

        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository
            {
                GitHubId = RepoId, Name = "repo", FullName = "owner/repo", OwnerLogin = "owner", IsPrivate = false,
            }, Repository.DocumentId(RepoId));
            await seed.StoreAsync(new Commit { Sha = Sha, Repository = Repository.DocumentId(RepoId), LatestBuildId = buildId }, commitId);

            // The build measured only a.cs …
            await seed.StoreAsync(new BuildTreeSummary
            {
                BuildId = buildId,
                Files = [new TreeFileSummary { Path = "src/a.cs", LinesCovered = 1, LinesCoverable = 2 }],
            }, BuildTreeSummary.DocumentId(buildId));

            // … the assembly also carries b.cs from an earlier commit.
            await seed.StoreAsync(new CommitAssembly { Commit = commitId, Sha = Sha, Completeness = CommitAssembly.Complete }, assemblyId);
            await seed.StoreAsync(new BuildTreeSummary
            {
                BuildId = assemblyId,
                Files =
                [
                    new TreeFileSummary { Path = "src/a.cs", LinesCovered = 1, LinesCoverable = 2, Origin = FileOrigin.Measured },
                    new TreeFileSummary { Path = "src/b.cs", LinesCovered = 3, LinesCoverable = 4, Origin = FileOrigin.Carried, CarriedFromSha = "base" },
                ],
            }, CommitAssembly.TreeDocumentId(commitId));
            await seed.StoreAsync(new FileCoverage
            {
                BuildId = buildId, Path = "src/b.cs",
                Lines = [new LineCoverage { Number = 1, Hits = 1, Status = LineStatus.Covered }],
                Origin = new FileOrigin { Kind = FileOrigin.Carried, FromSha = "base", OriginSha = "base" },
            }, CommitAssembly.FileDocumentId(commitId, "src/b.cs"));
            await seed.SaveChangesAsync();
        }
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);

        var tree = (BrowseController.TreeResponse)((OkObjectResult)
            (await controller.GetTree("owner", "repo", Sha, path: "src", flag: null, CancellationToken.None)).Result!).Value!;
        tree.BuildId.Should().Be(assemblyId);
        var entries = tree.Entries.ToDictionary(e => e.Name);
        entries.Should().HaveCount(2);
        entries["a.cs"].Origin.Should().Be(FileOrigin.Measured);
        entries["b.cs"].Origin.Should().Be(FileOrigin.Carried);
        entries["b.cs"].CarriedFromSha.Should().Be("base");

        var file = (await controller.GetFile("owner", "repo", Sha, "src/b.cs", CancellationToken.None)).Result;
        file.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Without_an_assembly_the_latest_build_is_still_the_source()
    {
        using var store = GetDocumentStore();
        var commitId = Commit.DocumentId(RepoId, Sha);
        var buildId = Build.DocumentId(RepoId, Sha, runId: 7, runAttempt: 1);

        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository
            {
                GitHubId = RepoId, Name = "repo", FullName = "owner/repo", OwnerLogin = "owner", IsPrivate = false,
            }, Repository.DocumentId(RepoId));
            await seed.StoreAsync(new Commit { Sha = Sha, Repository = Repository.DocumentId(RepoId), LatestBuildId = buildId }, commitId);
            await seed.StoreAsync(new BuildTreeSummary
            {
                BuildId = buildId,
                Files = [new TreeFileSummary { Path = "src/a.cs", LinesCovered = 1, LinesCoverable = 2 }],
            }, BuildTreeSummary.DocumentId(buildId));
            await seed.SaveChangesAsync();
        }
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var tree = (BrowseController.TreeResponse)((OkObjectResult)
            (await CreateController(session).GetTree("owner", "repo", Sha, path: null, flag: null, CancellationToken.None)).Result!).Value!;

        tree.BuildId.Should().Be(buildId);
        tree.Entries.Single().Origin.Should().BeNull();
    }
}
