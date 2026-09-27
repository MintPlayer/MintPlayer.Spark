using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Feedback;

/// <summary>
/// What the check-run publisher does when the forge says no.
/// </summary>
/// <remarks>
/// <see cref="PublishFeedbackRecipientGuardTests"/> covers the entry guards over the real GitHub
/// chain. These cover the outcomes once the forge has been asked, over a scripted integration: a
/// permission the forge will not grant is terminal (retrying it forever was the failure mode), a
/// transient error backs off exponentially, and the fifth failure gives up.
/// </remarks>
public class PublishFeedbackRecipientFailureTests : CoverageRavenTest
{
    private const long RepoId = 7400;
    private const string Sha = "feedfacefeedfacefeedfacefeedfacefeedface";

    private static string CommitId => Commit.DocumentId(EForgeProvider.GitHub, RepoId, Sha);

    private static string BuildId => Build.DocumentId(EForgeProvider.GitHub, RepoId, Sha, 1, 1);

    private static async Task SeedAsync(IDocumentStore store, bool withCommit = true, int attempts = 0, int? pullRequest = null)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new Repository
        {
            GitHubId = RepoId, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme", DefaultBranch = "main",
        }, Repository.DocumentId(EForgeProvider.GitHub, RepoId));
        if (withCommit)
            await seed.StoreAsync(new Commit
            {
                Repository = Repository.DocumentId(EForgeProvider.GitHub, RepoId), Sha = Sha, PullRequestNumber = pullRequest,
            }, CommitId);
        await seed.StoreAsync(new Build
        {
            Commit = CommitId, Status = "Finalized", CiRunId = 1, CiRunAttempt = 1, Run = Build.ComposeRun(1, 1),
            Coverage = new CoverageSummary(),
            Feedback = attempts == 0 ? null : new BuildFeedback { Attempts = attempts, State = "Retry" },
        }, BuildId);
        await seed.SaveChangesAsync();
    }

    private static IForgeIntegration Forge()
    {
        var forge = Substitute.For<IForgeIntegration>();
        forge.Provider.Returns(EForgeProvider.GitHub);
        forge.CheckAccessAsync(Arg.Any<Repository>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(ForgeAccess.Yes));
        forge.GetFileContentAsync(Arg.Any<Repository>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        return forge;
    }

    private static async Task<BuildFeedback?> PublishAsync(IDocumentStore store, IForgeIntegration forge)
    {
        var baseResolver = Substitute.For<IBaseResolver>();
        baseResolver.ResolveAsync(Arg.Any<Repository>(), Arg.Any<Commit>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ResolvedBase(null, null, ResolvedBase.None, null, null)));

        using (var session = store.OpenAsyncSession())
        {
            var recipient = new PublishFeedbackRecipient(
                session, baseResolver, new SingleForgeResolver(forge),
                new ConfigurationBuilder().Build(), NullLogger<PublishFeedbackRecipient>.Instance);
            await recipient.HandleAsync(new PublishFeedbackMessage { BuildId = BuildId });
        }

        using var check = store.OpenAsyncSession();
        return (await check.LoadAsync<Build>(BuildId)).Feedback;
    }

    [Fact]
    public async Task A_build_whose_commit_is_gone_publishes_nothing()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, withCommit: false);
        var forge = Forge();

        (await PublishAsync(store, forge)).Should().BeNull();
        await forge.DidNotReceiveWithAnyArgs().CheckAccessAsync(default!, default);
    }

    [Fact]
    public async Task A_permission_the_forge_will_not_grant_is_terminal_not_retried()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        var forge = Forge();
        forge.PublishStatusAsync(default!, default!, default!, default!, default, default).ReturnsForAnyArgs<Task<long>>(
            _ => throw new ForgeAccessDeniedException("The installation has not accepted checks:write."));

        var feedback = await PublishAsync(store, forge);

        feedback!.State.Should().Be("Unavailable");
        feedback.Error.Should().Be("The installation has not accepted checks:write.");
        feedback.NextAttemptAtUtc.HasValue.Should().BeFalse();
        feedback.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task A_transient_failure_backs_off_and_is_retried()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, attempts: 1);
        var forge = Forge();
        forge.PublishStatusAsync(default!, default!, default!, default!, default, default).ReturnsForAnyArgs<Task<long>>(
            _ => throw new HttpRequestException("502 Bad Gateway"));

        var before = DateTime.UtcNow;
        var feedback = await PublishAsync(store, forge);

        feedback!.State.Should().Be("Retry");
        feedback.Attempts.Should().Be(2);
        feedback.Error.Should().Be("502 Bad Gateway");
        // 2^attempts minutes: the second failure waits four.
        feedback.NextAttemptAtUtc!.Value.Should().BeOnOrAfter(before.AddMinutes(4));
        feedback.NextAttemptAtUtc!.Value.Should().BeOnOrBefore(DateTime.UtcNow.AddMinutes(4));
    }

    [Fact]
    public async Task The_fifth_failure_gives_up()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, attempts: 4);
        var forge = Forge();
        forge.PublishStatusAsync(default!, default!, default!, default!, default, default).ReturnsForAnyArgs<Task<long>>(
            _ => throw new HttpRequestException("still failing"));

        var feedback = await PublishAsync(store, forge);

        feedback!.State.Should().Be("Failed");
        feedback.Attempts.Should().Be(5);
        feedback.NextAttemptAtUtc.HasValue.Should().BeFalse();
    }

    /// <summary>A pull-request commit also gets its comment, after the check runs are saved.</summary>
    [Fact]
    public async Task A_posted_pull_request_build_also_publishes_its_comment()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, pullRequest: 17);
        var forge = Forge();
        forge.PublishStatusAsync(default!, default!, default!, default!, default, default).ReturnsForAnyArgs(Task.FromResult(99L));

        var feedback = await PublishAsync(store, forge);

        feedback!.State.Should().Be("Posted");
        feedback.ProjectCheckRunId.Should().Be(99L);
        await forge.Received(1).PublishCommentAsync(
            Arg.Any<Repository>(), 17, Sha, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
