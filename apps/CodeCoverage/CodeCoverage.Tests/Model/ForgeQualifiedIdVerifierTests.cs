using CodeCoverage.Entities;
using CodeCoverage.Migrations;
using CodeCoverage.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// The M6d verifier — does it actually notice the things the migration's guard cannot?
/// </summary>
/// <remarks>
/// ⚠️ <b>A verifier that only ever passes is worse than none</b>, because it converts "nobody
/// checked" into "we checked and it was fine". So every test here seeds a <em>specific</em> defect
/// and asserts the corresponding check fails — the clean-database case is one test out of several,
/// not the whole file.
/// </remarks>
public class ForgeQualifiedIdVerifierTests : CoverageRavenTest
{
    private const long RepositoryId = 204431316;
    private static readonly string Sha = new('a', 40);

    private static async Task SeedCleanAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();

        await session.StoreAsync(new Account { GitHubId = 7, Login = "MintPlayer" }, "Accounts/github/7");
        await session.StoreAsync(new Repository
        {
            GitHubId = RepositoryId,
            Name = "MintPlayer.Spark",
            FullName = "MintPlayer/MintPlayer.Spark",
            OwnerLogin = "MintPlayer",
            Account = "Accounts/github/7",
        }, $"Repositories/github/{RepositoryId}");
        await session.StoreAsync(new Commit
        {
            Sha = Sha,
            Repository = $"Repositories/github/{RepositoryId}",
        }, $"Commits/github/{RepositoryId}/{Sha}");

        await session.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<ForgeQualifiedIdVerifier.Finding>> VerifyAsync(IDocumentStore store)
    {
        WaitForIndexing(store);
        return await ForgeQualifiedIdVerifier.VerifyAsync(store);
    }

    private static ForgeQualifiedIdVerifier.Finding Check(
        IReadOnlyList<ForgeQualifiedIdVerifier.Finding> findings, string contains)
        => Assert.Single(findings, f => f.Check.Contains(contains, StringComparison.Ordinal));

    [Fact]
    public async Task A_correctly_migrated_database_passes_every_check()
    {
        using var store = GetDocumentStore();
        await SeedCleanAsync(store);

        var findings = await VerifyAsync(store);

        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.True(f.Ok, $"{f.Check}: {f.Detail}"));
    }

    [Fact]
    public async Task A_surviving_legacy_id_is_reported()
    {
        using var store = GetDocumentStore();
        await SeedCleanAsync(store);
        using (var seed = store.OpenAsyncSession())
        {
            // The partial-migration case the migration's own guard passes: SOME documents
            // qualified, so `qualified == 0` is false and it happily deleted the rest.
            await seed.StoreAsync(new Commit { Sha = "b", Repository = $"Repositories/github/{RepositoryId}" },
                $"Commits/{RepositoryId}/b");
            await seed.SaveChangesAsync();
        }

        var findings = await VerifyAsync(store);

        Assert.False(Check(findings, "Commits: no legacy ids").Ok);
    }

    /// <summary>
    /// ⚠️ The three the first migration actually missed, each asserted by name — these were live
    /// dangling references on production, not hypotheticals.
    /// </summary>
    [Fact]
    public async Task An_unqualified_pull_request_feedback_reference_is_reported()
    {
        using var store = GetDocumentStore();
        await SeedCleanAsync(store);
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(
                new PullRequestFeedback { Repository = $"Repositories/{RepositoryId}", PullRequestNumber = 79 },
                $"PullRequestFeedbacks/github/{RepositoryId}/79");
            await seed.SaveChangesAsync();
        }

        var findings = await VerifyAsync(store);

        Assert.False(Check(findings, "PullRequestFeedbacks.Repository").Ok);
    }

    [Fact]
    public async Task An_unqualified_carry_forward_provenance_is_reported()
    {
        using var store = GetDocumentStore();
        await SeedCleanAsync(store);
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new FileCoverage
            {
                Path = "src/a.cs",
                BuildId = $"Commits/github/{RepositoryId}/{Sha}/builds/1-1",
                Origin = new FileOrigin { Kind = FileOrigin.Carried, FromBuildId = $"Commits/{RepositoryId}/x/builds/9-1" },
            }, $"Commits/github/{RepositoryId}/{Sha}/builds/1-1/files/abc");
            await seed.SaveChangesAsync();
        }

        var findings = await VerifyAsync(store);

        Assert.False(Check(findings, "FileCoverages.Origin.FromBuildId").Ok);
    }

    [Fact]
    public async Task An_unqualified_board_account_reference_is_reported()
    {
        using var store = GetDocumentStore();
        await SeedCleanAsync(store);
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new GitHubProject
            {
                NodeId = "PVT_kwDO", Number = 1, Name = "Roadmap",
                OwnerLogin = "MintPlayer", Account = "Accounts/7",
            }, "GitHubProjects/PVT_kwDO");
            await seed.SaveChangesAsync();
        }

        var findings = await VerifyAsync(store);

        Assert.False(Check(findings, "GitHubProjects.Account").Ok);
    }

    /// <summary>
    /// A build whose session names an attachment that is not there — the case the migration makes
    /// possible by copying attachments with three silent <c>continue</c>s and then deleting the
    /// source documents.
    /// </summary>
    [Fact]
    public async Task A_missing_report_attachment_is_reported()
    {
        using var store = GetDocumentStore();
        await SeedCleanAsync(store);
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Build
            {
                Commit = $"Commits/github/{RepositoryId}/{Sha}",
                CiRunId = 1,
                CiRunAttempt = 1,
                Sessions = [new BuildSession { SessionId = "s1", RawFileNames = ["sessions/s1/0-lcov.info"] }],
            }, $"Commits/github/{RepositoryId}/{Sha}/builds/1-1");
            await seed.SaveChangesAsync();
        }

        var findings = await VerifyAsync(store);

        Assert.False(Check(findings, "Attachments named by build sessions").Ok);
    }

    /// <summary>
    /// The repair migration makes the verifier pass — the two halves of M6d agreeing, which is the
    /// only thing that shows the verifier is checking the same property the migration repairs.
    /// </summary>
    [Fact]
    public async Task The_repair_migration_makes_the_verifier_pass()
    {
        using var store = GetDocumentStore();
        await SeedCleanAsync(store);
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(
                new PullRequestFeedback { Repository = $"Repositories/{RepositoryId}", PullRequestNumber = 79 },
                $"PullRequestFeedbacks/github/{RepositoryId}/79");
            await seed.StoreAsync(new GitHubProject
            {
                NodeId = "PVT_kwDO", Number = 1, Name = "Roadmap",
                OwnerLogin = "MintPlayer", Account = "Accounts/7",
            }, "GitHubProjects/PVT_kwDO");
            await seed.SaveChangesAsync();
        }

        Assert.False(Check(await VerifyAsync(store), "PullRequestFeedbacks.Repository").Ok);

        await new M_202609220900_ForgeQualifiedReferencesTheFirstPassMissed(
            store, NullLogger<M_202609220900_ForgeQualifiedReferencesTheFirstPassMissed>.Instance)
            .UpAsync(CancellationToken.None);

        var after = await VerifyAsync(store);
        Assert.True(Check(after, "PullRequestFeedbacks.Repository").Ok);
        Assert.True(Check(after, "GitHubProjects.Account").Ok);
    }
}
