using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Feedback;

/// <summary>
/// The guards on the check-run publisher — the entry conditions it refuses on, before any GitHub
/// call is attempted.
/// <para>
/// This recipient was at <b>0% coverage</b>, and it is one of the five whose queue was dead in
/// production, so the code path has effectively never run there. Its GitHub-calling half needs an
/// Octokit harness; its guards do not, and they are where the damaging mistakes live: publishing a
/// check run for an unfinalized build would post numbers that are still changing, and treating a
/// missing App installation as a transient error would retry it forever.
/// </para>
/// </summary>
public class PublishFeedbackRecipientGuardTests : CoverageRavenTest
{
    private const long RepoId = 7300;
    private const string Sha = "abcdef0123456789abcdef0123456789abcdef01";

    private static PublishFeedbackRecipient CreateRecipient(IAsyncDocumentSession session)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(session);
        services.AddSingleton(Substitute.For<IBaseResolver>());
        services.AddSingleton(Substitute.For<IGitHubInstallationService>());
        services.AddSingleton(Substitute.For<IGitHubContentService>());
        services.AddSingleton(Substitute.For<IPullRequestCommentPublisher>());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<PublishFeedbackRecipient>();

        return services.BuildServiceProvider().GetRequiredService<PublishFeedbackRecipient>();
    }

    private static string BuildId => Build.DocumentId(RepoId, Sha, 1, 1);

    /// <summary>
    /// Seeds a commit, its repository, and a build in the given status. <paramref name="account"/>
    /// controls whether the repository resolves to an account with a GitHub App installation.
    /// </summary>
    private static async Task SeedAsync(IDocumentStore store, string buildStatus, bool withInstallation, bool withAccount = true)
    {
        using var session = store.OpenAsyncSession();

        if (withAccount)
        {
            await session.StoreAsync(new Account
            {
                GitHubId = 55,
                Login = "acme",
                InstallationId = withInstallation ? 9001 : null,
            }, Account.DocumentId(55));
        }

        await session.StoreAsync(new Repository
        {
            GitHubId = RepoId,
            Name = "widget",
            FullName = "acme/widget",
            OwnerLogin = "acme",
            Account = withAccount ? Account.DocumentId(55) : null,
            DefaultBranch = "main",
        }, Repository.DocumentId(RepoId));

        var commitId = Commit.DocumentId(RepoId, Sha);
        await session.StoreAsync(new Commit
        {
            Sha = Sha,
            Repository = Repository.DocumentId(RepoId),
            FirstSeenAtUtc = DateTimeOffset.UtcNow,
        }, commitId);

        await session.StoreAsync(new Build
        {
            Commit = commitId,
            CiRunId = 1,
            CiRunAttempt = 1,
            Status = buildStatus,
        }, BuildId);

        await session.SaveChangesAsync();
    }

    /// <summary>
    /// A build that is not Finalized is still accumulating sessions. Posting a check run for it
    /// would publish numbers that are about to change, and the run would then disagree with the
    /// badge — so the recipient returns without recording anything at all.
    /// </summary>
    [Theory]
    [InlineData("Open")]
    [InlineData("Parsing")]
    public async Task An_unfinalized_build_is_ignored_entirely(string status)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, status, withInstallation: true);

        using (var session = store.OpenAsyncSession())
            await CreateRecipient(session).HandleAsync(new PublishFeedbackMessage { BuildId = BuildId });

        using var verify = store.OpenAsyncSession();
        var build = await verify.LoadAsync<Build>(BuildId);

        // Not merely "no check run" — no feedback state either. Recording a state here would make
        // the build look processed when it still has work coming.
        Assert.Null(build!.Feedback);
    }

    [Fact]
    public async Task A_message_for_a_build_that_no_longer_exists_is_a_no_op()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        // Must not throw: the build can be deleted between queueing and processing, and a recipient
        // that threw would retry until it dead-lettered.
        await CreateRecipient(session).HandleAsync(
            new PublishFeedbackMessage { BuildId = Build.DocumentId(999999, Sha, 1, 1) });
    }

    /// <summary>
    /// No App installation is a <b>terminal</b> state, not a transient one.
    /// <para>
    /// The distinction is the whole point: <c>NextAttemptAtUtc</c> is cleared, so this never
    /// retries. An installation does not come back on its own, and retrying would burn the queue
    /// forever on a repository whose owner has simply uninstalled the App. The reason is recorded
    /// on the build so the UI can say why no checks appeared, rather than showing nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_repository_with_no_App_installation_is_marked_Unavailable_and_never_retried()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, "Finalized", withInstallation: false);

        using (var session = store.OpenAsyncSession())
            await CreateRecipient(session).HandleAsync(new PublishFeedbackMessage { BuildId = BuildId });

        using var verify = store.OpenAsyncSession();
        var feedback = (await verify.LoadAsync<Build>(BuildId))!.Feedback;

        Assert.NotNull(feedback);
        Assert.Equal("Unavailable", feedback!.State);
        Assert.Null(feedback.NextAttemptAtUtc);
        Assert.Contains("installation", feedback.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Same terminal outcome when the repository has no account at all — a shape that exists for
    /// repositories recorded before accounts were linked.
    /// </summary>
    [Fact]
    public async Task A_repository_with_no_account_is_also_Unavailable()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, "Finalized", withInstallation: false, withAccount: false);

        using (var session = store.OpenAsyncSession())
            await CreateRecipient(session).HandleAsync(new PublishFeedbackMessage { BuildId = BuildId });

        using var verify = store.OpenAsyncSession();
        var feedback = (await verify.LoadAsync<Build>(BuildId))!.Feedback;

        Assert.NotNull(feedback);
        Assert.Equal("Unavailable", feedback!.State);
        Assert.Null(feedback.NextAttemptAtUtc);
    }
}
