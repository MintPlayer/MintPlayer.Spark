using CodeCoverage.Entities;
using CodeCoverage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// The three reference fields the forge-qualifying pass re-keyed ids around but never rewrote.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>These shipped dangling to production.</b> The first migration rewrote the references it
/// enumerated and deleted the documents behind the ones it did not. A dangling reference is not an
/// error in RavenDB — <c>LoadAsync</c> simply returns null — so each one surfaces as a different,
/// smaller-looking bug: an empty panel, a missing avatar, a pull-request comment that re-posts
/// instead of editing itself.
/// </para>
/// <para>
/// Every test here seeds the <b>broken</b> shape rather than a pristine one, because that is the
/// only state this migration will ever meet: production has already been through the first pass.
/// </para>
/// </remarks>
public class ForgeQualifiedReferenceRepairTests : CoverageRavenTest
{
    private const long RepositoryId = 204431316;
    private const long AccountId = 7;

    private static Task RunAsync(IDocumentStore store)
        => new M_202609220900_ForgeQualifiedReferencesTheFirstPassMissed(
            store,
            NullLogger<M_202609220900_ForgeQualifiedReferencesTheFirstPassMissed>.Instance)
        .UpAsync(CancellationToken.None);

    [Fact]
    public async Task A_pull_request_feedbacks_repository_reference_is_re_qualified()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new PullRequestFeedback
            {
                // The shape the first pass left: the id moved, the reference did not.
                Repository = $"Repositories/{RepositoryId}",
                PullRequestNumber = 79,
            }, $"PullRequestFeedbacks/github/{RepositoryId}/79");
            await seed.SaveChangesAsync();
        }

        await RunAsync(store);

        using var verify = store.OpenAsyncSession();
        var feedback = await verify.LoadAsync<PullRequestFeedback>($"PullRequestFeedbacks/github/{RepositoryId}/79");
        Assert.Equal($"Repositories/github/{RepositoryId}", feedback.Repository);
    }

    [Fact]
    public async Task A_file_coverages_carry_forward_provenance_is_re_qualified()
    {
        using var store = GetDocumentStore();
        var sha = new string('a', 40);
        var id = $"Commits/github/{RepositoryId}/{sha}/builds/1-1/files/abc";

        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new FileCoverage
            {
                Path = "src/a.cs",
                BuildId = $"Commits/github/{RepositoryId}/{sha}/builds/1-1",
                Origin = new FileOrigin
                {
                    Kind = FileOrigin.Carried,
                    FromBuildId = $"Commits/{RepositoryId}/{new string('b', 40)}/builds/9-1",
                },
            }, id);
            await seed.SaveChangesAsync();
        }

        await RunAsync(store);

        using var verify = store.OpenAsyncSession();
        var file = await verify.LoadAsync<FileCoverage>(id);
        Assert.StartsWith($"Commits/github/{RepositoryId}/", file.Origin!.FromBuildId, StringComparison.Ordinal);
    }

    /// <summary>
    /// A measured file has no <c>Origin</c> at all, and the patch must skip it rather than fail the
    /// whole document — which is what assigning through a null would do.
    /// </summary>
    [Fact]
    public async Task A_file_with_no_carry_forward_provenance_is_left_alone()
    {
        using var store = GetDocumentStore();
        var id = $"Commits/github/{RepositoryId}/{new string('a', 40)}/builds/1-1/files/abc";

        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new FileCoverage { Path = "src/a.cs", Origin = null }, id);
            await seed.SaveChangesAsync();
        }

        await RunAsync(store);

        using var verify = store.OpenAsyncSession();
        var file = await verify.LoadAsync<FileCoverage>(id);
        Assert.NotNull(file);
        Assert.Null(file.Origin);
    }

    [Fact]
    public async Task A_project_boards_account_reference_is_re_qualified()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new GitHubProject
            {
                NodeId = "PVT_kwDO",
                Number = 1,
                Name = "Roadmap",
                OwnerLogin = "MintPlayer",
                Account = $"Accounts/{AccountId}",
            }, "GitHubProjects/PVT_kwDO");
            await seed.SaveChangesAsync();
        }

        await RunAsync(store);

        using var verify = store.OpenAsyncSession();
        var board = await verify.LoadAsync<GitHubProject>("GitHubProjects/PVT_kwDO");
        Assert.Equal($"Accounts/github/{AccountId}", board.Account);

        // ⚠️ The board's OWN id must not move — it is node-id-derived, which is exactly why the
        // first migration skipped this collection and why only the reference had to follow.
        Assert.Equal("GitHubProjects/PVT_kwDO", board.Id);
    }

    /// <summary>
    /// Running twice changes nothing the second time. Production has already been half-converted, so
    /// a re-run is the normal case, not an edge one.
    /// </summary>
    [Fact]
    public async Task Running_it_again_is_a_no_op()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new PullRequestFeedback
            {
                Repository = $"Repositories/{RepositoryId}",
                PullRequestNumber = 79,
            }, $"PullRequestFeedbacks/github/{RepositoryId}/79");
            await seed.SaveChangesAsync();
        }

        await RunAsync(store);
        await RunAsync(store);

        using var verify = store.OpenAsyncSession();
        var feedback = await verify.LoadAsync<PullRequestFeedback>($"PullRequestFeedbacks/github/{RepositoryId}/79");

        // Not merely "still qualified" — never DOUBLE-qualified, which is the failure a naive
        // prefix-prepend would produce and which no later run could undo.
        Assert.Equal($"Repositories/github/{RepositoryId}", feedback.Repository);
        Assert.DoesNotContain("github/github", feedback.Repository, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reference that is already correct is not touched — the guard that makes the re-run safe.
    /// </summary>
    [Fact]
    public async Task An_already_qualified_reference_is_untouched()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new PullRequestFeedback
            {
                Repository = $"Repositories/github/{RepositoryId}",
                PullRequestNumber = 80,
            }, $"PullRequestFeedbacks/github/{RepositoryId}/80");
            await seed.SaveChangesAsync();
        }

        await RunAsync(store);

        using var verify = store.OpenAsyncSession();
        var feedback = await verify.LoadAsync<PullRequestFeedback>($"PullRequestFeedbacks/github/{RepositoryId}/80");
        Assert.Equal($"Repositories/github/{RepositoryId}", feedback.Repository);
    }
}
