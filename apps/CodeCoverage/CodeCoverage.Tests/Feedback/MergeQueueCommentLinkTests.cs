using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using CodeCoverage.Tests.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Feedback;

/// <summary>
/// The coverage comment's line to the merge queue's check (merge-queue PRD §0 O32, D89), through the two recipients that
/// write the comment: only when the default branch's <c>coverage.yml</c> turns a working queue on (O16) and the pull
/// request targets one of its branches, and pointing at the pull request's latest queue check when the saga recorded one.
/// </summary>
public class MergeQueueCommentLinkTests : CoverageRavenTest
{
    private const long RepoId = 7410;
    private const long AccountId = 57;
    private const string Sha = "2234567890abcdef1234567890abcdef12345678";
    private const int PullRequest = 42;
    private const string Checks = "https://github.com/acme/widget/pull/42/checks";
    private const string QueueOn = "mergeQueue:\n  enabled: true\n";

    private static string RepositoryId => Repository.DocumentId(EForgeProvider.GitHub, RepoId);
    private static string BuildId => Build.DocumentId(EForgeProvider.GitHub, RepoId, Sha, 1, 1);

    private static async Task SeedAsync(IDocumentStore store, string baseRef = "main", long? latestCheckRunId = null)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Account { GitHubId = AccountId, Login = "acme", InstallationId = 9003 }, Account.DocumentId(EForgeProvider.GitHub, AccountId));
        await session.StoreAsync(new Repository
        {
            GitHubId = RepoId, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme",
            Account = Account.DocumentId(EForgeProvider.GitHub, AccountId), DefaultBranch = "main",
            LatestCoverage = new CoverageSummary { LinesCovered = 1, LinesCoverable = 2 },
        }, RepositoryId);

        var commitId = Commit.DocumentId(EForgeProvider.GitHub, RepoId, Sha);
        await session.StoreAsync(new Commit
        {
            Sha = Sha, Repository = RepositoryId, Branch = "feature/x", PullRequestNumber = PullRequest, PullRequestBaseRef = baseRef,
            FirstSeenAtUtc = DateTimeOffset.UtcNow, Coverage = new CoverageSummary { LinesCovered = 1, LinesCoverable = 2 },
        }, commitId);
        await session.StoreAsync(new Build
        {
            Commit = commitId, CiRunId = 1, CiRunAttempt = 1, Status = "Finalized",
            Coverage = new CoverageSummary { LinesCovered = 1, LinesCoverable = 2 },
        }, BuildId);

        if (latestCheckRunId is { } checkRunId)
        {
            var id = MergeQueueLatestCheck.DocumentId(RepositoryId, PullRequest);
            await session.StoreAsync(new MergeQueueLatestCheck
            {
                Repository = RepositoryId, Number = PullRequest, Sha = Sha, CheckRunId = checkRunId, UpdatedAtUtc = DateTime.UtcNow,
            }, id);
        }
        await session.SaveChangesAsync();
    }

    private static ScriptedDiffService Forge(string? yml)
    {
        var forge = new ScriptedDiffService();
        if (yml is not null) forge.Files[$"main/{CoverageYml.FileName}"] = yml;
        return forge;
    }

    /// <summary>The comment the finalize path writes (<see cref="PublishFeedbackRecipient"/>).</summary>
    private static async Task<string> PublishedCommentAsync(IDocumentStore store, ScriptedDiffService forge)
    {
        using var session = store.OpenAsyncSession();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(session);
        var baseResolver = Substitute.For<IBaseResolver>();
        baseResolver.ResolveAsync(default!, default!, default, default)
            .ReturnsForAnyArgs(Task.FromResult(new ResolvedBase(null, null, ResolvedBase.None, null, null)));
        services.AddSingleton(baseResolver);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IForgeIntegration>(forge);
        services.AddSingleton<IForgeIntegrationResolver>(forge);
        services.AddScoped<PublishFeedbackRecipient>();

        await services.BuildServiceProvider().GetRequiredService<PublishFeedbackRecipient>().HandleAsync(new PublishFeedbackMessage { BuildId = BuildId });
        return forge.Comments.Should().ContainSingle().Which.Body;
    }

    [Fact]
    public async Task The_comment_links_the_pull_requests_latest_queue_check()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, latestCheckRunId: 4242);

        var body = await PublishedCommentAsync(store, Forge(QueueOn));

        body.Should().Contain($"**Merge queue:** [manage this pull request on its check →]({Checks}?check_run_id=4242)");
    }

    [Fact]
    public async Task Without_a_recorded_check_the_comment_links_the_pull_requests_checks()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);

        var body = await PublishedCommentAsync(store, Forge(QueueOn));

        body.Should().Contain($"[manage this pull request on its check →]({Checks})");
    }

    [Theory]
    [InlineData(null, "main")]
    [InlineData("mergeQueue:\n  enabled: false\n", "main")]
    [InlineData("mergeQueue:\n  enabled: true\n  autoResolve:\n    - path: \"**/modelHashes.json\"\n", "main")]
    [InlineData(QueueOn, "develop")]
    public async Task No_merge_queue_line_without_a_working_queue_on_the_default_branch_or_for_a_base_it_does_not_serve(string? yml, string baseRef)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, baseRef, latestCheckRunId: 4242);

        var body = await PublishedCommentAsync(store, Forge(yml));

        body.Should().NotContain("Merge queue");
    }

    /// <summary>The comment posted when the pull request opens (<see cref="OpenPullRequestCommentRecipient"/>) carries it too.</summary>
    [Fact]
    public async Task The_pending_comment_links_the_queue_check_too()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, latestCheckRunId: 4243);
        var forge = Forge(QueueOn);

        using (var session = store.OpenAsyncSession())
        {
            var recipient = new OpenPullRequestCommentRecipient(session, forge, new ConfigurationBuilder().Build(),
                NullLogger<OpenPullRequestCommentRecipient>.Instance);
            await recipient.HandleAsync(new OpenPullRequestCommentMessage
            {
                RepositoryGitHubId = RepoId, PullRequestNumber = PullRequest, HeadSha = Sha, BaseRef = "main",
            });
        }

        forge.Comments.Should().ContainSingle().Which.Body.Should().Contain($"({Checks}?check_run_id=4243)");
    }
}
