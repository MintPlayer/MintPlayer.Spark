using CodeCoverage.Forge;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;
using CodeCoverage.Tests.Services;

namespace CodeCoverage.Tests.Feedback;

/// <summary>
/// The three gates on the publish-on-open path. Each exists to avoid a comment
/// that could never become useful, and each was a real observation rather than
/// a hypothetical.
/// </summary>
public class OpenPullRequestCommentRecipientTests : CoverageRavenTest
{
    private const long RepoId = 204431316;
    private const long AccountId = 42;
    private const int Pr = 79;
    private const string Sha = "79bc284939350991803acc84ced894ade844b9f0";

    /// <summary>
    /// The scripted forge stands in for the publisher now that this recipient goes through the
    /// forge seam. <c>AccessAvailable</c> replaces the old "does the account have an installation?"
    /// seeding: that was always a proxy for "can we write here?", and the seam asks it directly.
    /// </summary>
    private static ScriptedDiffService Forge(bool accessible = true)
        => new() { AccessAvailable = accessible };

    private static async Task Seed(
        IDocumentStore store,
        bool withInstallation,
        bool withRepositoryCoverage,
        bool withSideBranchCoverage = false)
    {
        using var seed = store.OpenAsyncSession();

        if (withInstallation)
            await seed.StoreAsync(new Account { GitHubId = AccountId, Login = "MintPlayer", InstallationId = 555 }, Account.DocumentId(EForgeProvider.GitHub, AccountId));

        await seed.StoreAsync(new Entities.Repository
        {
            GitHubId = RepoId,
            Name = "MintPlayer.Spark",
            FullName = "MintPlayer/MintPlayer.Spark",
            OwnerLogin = "MintPlayer",
            Account = withInstallation ? Account.DocumentId(EForgeProvider.GitHub, AccountId) : null,
            LatestCoverage = withRepositoryCoverage ? new CoverageSummary { LinesCovered = 80, LinesCoverable = 100 } : null,
        }, Entities.Repository.DocumentId(EForgeProvider.GitHub, RepoId));

        if (withSideBranchCoverage)
        {
            await seed.StoreAsync(new Entities.Commit
            {
                Sha = "aaa",
                Repository = Entities.Repository.DocumentId(EForgeProvider.GitHub, RepoId),
                Branch = "feature/x",
                AuthoredAt = DateTimeOffset.UtcNow,
                Coverage = new CoverageSummary { LinesCovered = 10, LinesCoverable = 100 },
            }, Entities.Commit.DocumentId(EForgeProvider.GitHub, RepoId, "aaa"));
        }

        await seed.SaveChangesAsync();
    }

    private static OpenPullRequestCommentRecipient Create(IAsyncDocumentSession session, ScriptedDiffService forge)
        => new(session, forge,
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Coverage:BaseUrl"] = "https://coverage.mintplayer.com" }).Build(),
            NullLogger<OpenPullRequestCommentRecipient>.Instance);

    private static OpenPullRequestCommentMessage Message(bool authorIsBot = false) => new()
    {
        RepositoryGitHubId = RepoId,
        PullRequestNumber = Pr,
        HeadSha = Sha,
        AuthorIsBot = authorIsBot,
    };

    [Fact]
    public async Task A_pull_request_on_a_covered_repository_gets_one_pending_comment()
    {
        using var store = GetDocumentStore();
        await Seed(store, withInstallation: true, withRepositoryCoverage: true);
        WaitForIndexing(store);

        var publisher = Forge();
        using var session = store.OpenAsyncSession();
        await Create(session, publisher).HandleAsync(Message());

        publisher.Comments.Select(c => c.Body).Should().ContainSingle();
        publisher.Comments[0].Body.Should().StartWith(PullRequestCommentRenderer.Marker);
        publisher.Comments[0].Body.Should().Contain("Waiting for coverage");
        publisher.Comments[0].Body.Should().Contain("79bc284");
    }

    /// <summary>
    /// H12. Measured: dependabot runs receive no repository secrets and this
    /// repo's PR workflow grants no id-token: write, so such a PR can never
    /// upload — the placeholder would say "waiting" forever.
    /// </summary>
    [Fact]
    public async Task A_bot_authored_pull_request_gets_nothing()
    {
        using var store = GetDocumentStore();
        await Seed(store, withInstallation: true, withRepositoryCoverage: true);
        WaitForIndexing(store);

        var publisher = Forge();
        using var session = store.OpenAsyncSession();
        await Create(session, publisher).HandleAsync(Message(authorIsBot: true));

        publisher.Comments.Select(c => c.Body).Should().BeEmpty();
    }

    /// <summary>
    /// H10. A repository that has never uploaded is not asking for a coverage
    /// bot on every pull request it opens.
    /// </summary>
    [Fact]
    public async Task A_repository_with_no_coverage_history_gets_nothing()
    {
        using var store = GetDocumentStore();
        await Seed(store, withInstallation: true, withRepositoryCoverage: false);
        WaitForIndexing(store);

        var publisher = Forge();
        using var session = store.OpenAsyncSession();
        await Create(session, publisher).HandleAsync(Message());

        publisher.Comments.Select(c => c.Body).Should().BeEmpty();
    }

    /// <summary>
    /// LatestCoverage only ever tracks the default branch, so a repository that
    /// has measured nothing but side branches still has history — it just costs
    /// an index query to find out.
    /// </summary>
    [Fact]
    public async Task Coverage_on_a_side_branch_alone_still_counts_as_history()
    {
        using var store = GetDocumentStore();
        await Seed(store, withInstallation: true, withRepositoryCoverage: false, withSideBranchCoverage: true);
        WaitForIndexing(store);

        var publisher = Forge();
        using var session = store.OpenAsyncSession();
        await Create(session, publisher).HandleAsync(Message());

        publisher.Comments.Select(c => c.Body).Should().ContainSingle();
    }

    /// <summary>
    /// OIDC-only repositories are a supported population and get no check-runs
    /// either; the comment degrades the same way, silently.
    /// </summary>
    [Fact]
    public async Task A_repository_with_no_forge_access_gets_nothing()
    {
        using var store = GetDocumentStore();
        await Seed(store, withInstallation: false, withRepositoryCoverage: true);
        WaitForIndexing(store);

        var publisher = Forge(accessible: false);
        using var session = store.OpenAsyncSession();
        await Create(session, publisher).HandleAsync(Message());

        publisher.Comments.Select(c => c.Body).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_repository_is_ignored_rather_than_throwing()
    {
        using var store = GetDocumentStore();
        var publisher = Forge();
        using var session = store.OpenAsyncSession();

        await Create(session, publisher).HandleAsync(new OpenPullRequestCommentMessage
        {
            RepositoryGitHubId = 999999,
            PullRequestNumber = 1,
            HeadSha = "abc",
        });

        publisher.Comments.Select(c => c.Body).Should().BeEmpty();
    }
}
