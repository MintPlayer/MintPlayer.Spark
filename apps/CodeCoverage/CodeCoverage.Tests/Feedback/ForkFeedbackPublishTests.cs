using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using CodeCoverage.Tests._Infrastructure;
using CodeCoverage.Tests.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Feedback;

/// <summary>
/// Fork coverage actually reaches the pull request.
/// </summary>
/// <remarks>
/// <para>
/// <c>ForkFeedbackTests</c> asserts the two pure functions in isolation. This one runs the recipient
/// end to end against an embedded RavenDB and a scripted forge, because the question that matters is
/// not "does <c>ToVerdict</c> force neutral" but <b>"does a fork build get a comment posted at
/// all"</b> — and that depends on the whole chain still reaching the comment branch, which nothing
/// else covers.
/// </para>
/// <para>
/// ⚠️ The failure this guards against is silence. Every gate in the recipient degrades quietly: no
/// installation records <c>Unavailable</c> and returns, an unfinalized build returns without a
/// trace. A fork build that stopped producing feedback would look exactly like a fork build that
/// produced feedback nobody looked at.
/// </para>
/// </remarks>
public class ForkFeedbackPublishTests : CoverageRavenTest
{
    private const long RepoId = 7400;
    private const long AccountId = 56;
    private const string Sha = "1234567890abcdef1234567890abcdef12345678";
    private const int PullRequest = 42;

    private static string BuildId => Build.DocumentId(EForgeProvider.GitHub, RepoId, Sha, 1, 1);

    private static (PublishFeedbackRecipient Recipient, ScriptedDiffService Forge) Create(IAsyncDocumentSession session)
    {
        var forge = new ScriptedDiffService();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(session);
        // A substitute that returns null makes BuildComparer throw; the real resolver always hands
        // back a ResolvedBase. "No base" is the honest scenario here anyway - a fork pull request
        // usually has no previously-covered commit to compare against.
        var baseResolver = Substitute.For<IBaseResolver>();
        baseResolver.ResolveAsync(default!, default!, default, default)
            .ReturnsForAnyArgs(Task.FromResult(new ResolvedBase(null, null, ResolvedBase.None, null, null)));
        services.AddSingleton(baseResolver);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IForgeIntegration>(forge);
        services.AddSingleton<IForgeIntegrationResolver>(forge);
        services.AddScoped<PublishFeedbackRecipient>();

        return (services.BuildServiceProvider().GetRequiredService<PublishFeedbackRecipient>(), forge);
    }

    private static async Task SeedAsync(IDocumentStore store, bool contributedFromFork)
    {
        using var session = store.OpenAsyncSession();

        await session.StoreAsync(new Account
        {
            GitHubId = AccountId,
            Login = "acme",
            InstallationId = 9002,
        }, Account.DocumentId(EForgeProvider.GitHub, AccountId));

        await session.StoreAsync(new Repository
        {
            GitHubId = RepoId,
            Name = "widget",
            FullName = "acme/widget",
            OwnerLogin = "acme",
            Account = Account.DocumentId(EForgeProvider.GitHub, AccountId),
            DefaultBranch = "main",
        }, Repository.DocumentId(EForgeProvider.GitHub, RepoId));

        var commitId = Commit.DocumentId(EForgeProvider.GitHub, RepoId, Sha);
        await session.StoreAsync(new Commit
        {
            Sha = Sha,
            Repository = Repository.DocumentId(EForgeProvider.GitHub, RepoId),
            Branch = "feature/x",
            // Set for both cases: an ordinary pull-request build has one too, so the fork flag is
            // the only difference between the two scenarios.
            PullRequestNumber = PullRequest,
            ContributedFromFork = contributedFromFork,
            FirstSeenAtUtc = DateTimeOffset.UtcNow,
            Coverage = new CoverageSummary { LinesCovered = 1, LinesCoverable = 2 },
        }, commitId);

        await session.StoreAsync(new Build
        {
            Commit = commitId,
            CiRunId = 1,
            CiRunAttempt = 1,
            Status = "Finalized",
            Coverage = new CoverageSummary { LinesCovered = 1, LinesCoverable = 2 },
        }, BuildId);

        await session.SaveChangesAsync();
    }

    private static async Task<(List<(int PullRequestNumber, string Sha, string Body)> Comments,
                               List<(string Sha, string Name, ForgeVerdict Verdict)> Statuses)>
        RunAsync(IDocumentStore store, bool contributedFromFork)
    {
        await SeedAsync(store, contributedFromFork);

        using var session = store.OpenAsyncSession();
        var (recipient, forge) = Create(session);
        await recipient.HandleAsync(new PublishFeedbackMessage { BuildId = BuildId });

        return (forge.Comments, forge.Statuses);
    }

    [Fact]
    public async Task A_fork_build_posts_its_comment_on_the_pull_request()
    {
        using var store = GetDocumentStore();
        var (comments, _) = await RunAsync(store, contributedFromFork: true);

        var comment = Assert.Single(comments);
        Assert.Equal(PullRequest, comment.PullRequestNumber);
        Assert.Equal(Sha, comment.Sha);
        Assert.Contains("Contributed from a fork", comment.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fork_builds_check_runs_are_published_and_both_are_neutral()
    {
        using var store = GetDocumentStore();
        var (_, statuses) = await RunAsync(store, contributedFromFork: true);

        // Both checks, not just one: a reviewer sees two rows and an absent one reads as "still
        // running" rather than "not applicable".
        Assert.Equal(2, statuses.Count);
        Assert.Contains(statuses, s => s.Name == "coverage/project");
        Assert.Contains(statuses, s => s.Name == "coverage/patch");
        Assert.All(statuses, s => Assert.Equal(EForgeOutcome.Neutral, s.Verdict.Outcome));
        Assert.All(statuses, s => Assert.Contains("fork", s.Verdict.Title, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The paired negative: the same build without the fork flag still behaves as it always did.
    /// Without this, a change that suppressed feedback entirely would pass the tests above.
    /// </summary>
    [Fact]
    public async Task A_first_party_pull_request_build_still_comments_without_the_fork_notice()
    {
        using var store = GetDocumentStore();
        var (comments, statuses) = await RunAsync(store, contributedFromFork: false);

        var comment = Assert.Single(comments);
        Assert.Equal(PullRequest, comment.PullRequestNumber);
        Assert.DoesNotContain("Contributed from a fork", comment.Body, StringComparison.Ordinal);

        Assert.Equal(2, statuses.Count);
        Assert.All(statuses, s => Assert.DoesNotContain("fork", s.Verdict.Title, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Feedback state is recorded either way, so the build does not look unprocessed and get
    /// re-picked by the retry cron job forever.
    /// </summary>
    [Fact]
    public async Task A_fork_build_records_its_feedback_state()
    {
        using var store = GetDocumentStore();
        await RunAsync(store, contributedFromFork: true);

        using var verify = store.OpenAsyncSession();
        var build = await verify.LoadAsync<Build>(BuildId);

        Assert.Equal("Posted", build.Feedback?.State);
        Assert.Null(build.Feedback?.NextAttemptAtUtc);
    }
}
