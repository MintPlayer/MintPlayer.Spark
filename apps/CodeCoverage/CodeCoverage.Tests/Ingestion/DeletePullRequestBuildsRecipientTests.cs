using CodeCoverage.Entities;
using CodeCoverage.Ingestion;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// Retention D4: a merged PR's build documents (builds, file coverage, tree
/// summaries) are deleted; the Commit survives with its display summary but
/// no dangling LatestBuildId; default-branch commits are never touched.
/// </summary>
public class DeletePullRequestBuildsRecipientTests : CoverageRavenTest
{
    private const long RepoGitHubId = 99;

    private static async Task<string> SeedPrCommit(IDocumentStore store, string sha, string branch, int prNumber)
    {
        using var session = store.OpenAsyncSession();
        var buildId = Build.DocumentId(RepoGitHubId, sha, 1, 1);
        await session.StoreAsync(new Commit
        {
            Sha = sha,
            Repository = Repository.DocumentId(RepoGitHubId),
            Branch = branch,
            PullRequestNumber = prNumber,
            Coverage = new CoverageSummary { LinesCovered = 1, LinesCoverable = 2 },
            LatestBuildId = buildId,
            FirstSeenAtUtc = DateTimeOffset.UtcNow,
        }, Commit.DocumentId(RepoGitHubId, sha));
        await session.StoreAsync(new Build { Commit = Commit.DocumentId(RepoGitHubId, sha), CiRunId = 1, CiRunAttempt = 1 }, buildId);
        await session.StoreAsync(new FileCoverage { Path = "libs/a/x.ts" }, FileCoverage.DocumentId(buildId, "libs/a/x.ts"));
        await session.StoreAsync(new BuildTreeSummary { BuildId = buildId }, BuildTreeSummary.DocumentId(buildId));
        await session.SaveChangesAsync();
        return buildId;
    }

    [Fact]
    public async Task Merged_pr_build_documents_are_deleted_and_the_commit_survives_clean()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository
            {
                GitHubId = RepoGitHubId, Name = "repo", FullName = "acme/repo",
                OwnerLogin = "acme", DefaultBranch = "master",
            }, Repository.DocumentId(RepoGitHubId));
            await seed.SaveChangesAsync();
        }
        var prBuildId = await SeedPrCommit(store, "feat0000", "feature", prNumber: 5);
        var masterBuildId = await SeedPrCommit(store, "mast0000", "master", prNumber: 5);
        WaitForIndexing(store);

        using (var session = store.OpenAsyncSession())
        {
            var recipient = new DeletePullRequestBuildsRecipient(session, NullLogger<DeletePullRequestBuildsRecipient>.Instance);
            await recipient.HandleAsync(new DeletePullRequestBuildsMessage { RepositoryGitHubId = RepoGitHubId, PullRequestNumber = 5 });
        }

        using var verify = store.OpenAsyncSession();
        (await verify.LoadAsync<Build>(prBuildId)).Should().BeNull();
        (await verify.LoadAsync<BuildTreeSummary>(BuildTreeSummary.DocumentId(prBuildId))).Should().BeNull();
        (await verify.LoadAsync<FileCoverage>(FileCoverage.DocumentId(prBuildId, "libs/a/x.ts"))).Should().BeNull();

        var prCommit = await verify.LoadAsync<Commit>(Commit.DocumentId(RepoGitHubId, "feat0000"));
        prCommit.Should().NotBeNull("the commit keeps its display summary");
        prCommit!.CodeCoverage.Should().NotBeNull();
        prCommit.LatestBuildId.Should().BeNull("nothing may dangle");

        // The default-branch commit — the repository's history — is untouched.
        (await verify.LoadAsync<Build>(masterBuildId)).Should().NotBeNull();
        var masterCommit = await verify.LoadAsync<Commit>(Commit.DocumentId(RepoGitHubId, "mast0000"));
        masterCommit!.LatestBuildId.Should().Be(masterBuildId);
    }

    [Fact]
    public async Task Other_pull_requests_data_is_untouched()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository
            {
                GitHubId = RepoGitHubId, Name = "repo", FullName = "acme/repo",
                OwnerLogin = "acme", DefaultBranch = "master",
            }, Repository.DocumentId(RepoGitHubId));
            await seed.SaveChangesAsync();
        }
        var otherBuildId = await SeedPrCommit(store, "other000", "another-feature", prNumber: 6);
        WaitForIndexing(store);

        using (var session = store.OpenAsyncSession())
        {
            var recipient = new DeletePullRequestBuildsRecipient(session, NullLogger<DeletePullRequestBuildsRecipient>.Instance);
            await recipient.HandleAsync(new DeletePullRequestBuildsMessage { RepositoryGitHubId = RepoGitHubId, PullRequestNumber = 5 });
        }

        using var verify = store.OpenAsyncSession();
        (await verify.LoadAsync<Build>(otherBuildId)).Should().NotBeNull();
    }
}
