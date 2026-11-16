using CodeCoverage.Commands;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using MintPlayer.Spark.Logging;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Commands;

/// <summary>
/// #660 G4/G5/R7: one record per comment, one current reaction replaced by id, an end only for the run that started it,
/// and bookkeeping that never fails the command.
/// </summary>
public class CommandLifecycleTests : CoverageRavenTest
{
    private const long Comment = 6001;

    private static readonly Repository Repo = new()
    {
        Id = Repository.DocumentId(EForgeProvider.GitHub, 6600), Provider = EForgeProvider.GitHub, GitHubId = 6600, FullName = "acme/widget",
    };

    private readonly FakeCommentReactions reactions = new();
    private readonly RecordingLoggerScopes<CommandLifecycle> logs = new();

    private async Task<(IDocumentStore, CommandLifecycle)> LifecycleAsync(int? retentionDays = null)
    {
        var store = GetDocumentStore();
        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Repository { Provider = Repo.Provider, GitHubId = Repo.GitHubId, FullName = Repo.FullName }, Repo.Id);
            await session.SaveChangesAsync();
        }
        return (store, CommandTestKit.Lifecycle(store, reactions, logs: logs, retentionDays: retentionDays));
    }

    [Fact]
    public async Task A_command_is_recorded_once_with_eyes_once_however_often_it_is_delivered()
    {
        var (store, lifecycle) = await LifecycleAsync();

        var first = await lifecycle.BeginAsync(Repo, 7, Comment, "/queue\nplease", "maintainer");
        var again = await lifecycle.BeginAsync(Repo, 7, Comment, "/queue\nplease", "maintainer");

        again.Should().Be(first);
        reactions.Added.Should().ContainSingle().Which.Reaction.Should().Be(EForgeReaction.Eyes);
        var record = await CommandTestKit.InvocationAsync(store, Repo, Comment);
        record!.Command.Should().Be("/queue", "only the first line is the command");
        record.Outcome.Should().Be(ECommandOutcome.Received);
        record.LogId.Should().Be(LogIds.ForKey(CommandInvocation.LogKeyOf(first!)));
        record.ExpiresAtUtc.HasValue.Should().BeFalse("G5: a command still going never expires");
    }

    [Fact]
    public async Task Without_a_comment_nothing_is_recorded_and_every_step_is_a_no_op()
    {
        var (_, lifecycle) = await LifecycleAsync();

        var id = await lifecycle.BeginAsync(Repo, 7, null, "/queue", "maintainer");
        await lifecycle.FinishAsync(id, ECommandOutcome.Done, "merged");

        id.Should().BeNull();
        lifecycle.OpenLog(id).Should().BeNull();
        reactions.Added.Should().BeEmpty();
        logs.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Each_state_replaces_the_previous_reaction_and_the_end_sets_the_expiry()
    {
        var (store, lifecycle) = await LifecycleAsync(retentionDays: 7);
        var id = await lifecycle.BeginAsync(Repo, 7, Comment, "/refresh", "maintainer");

        await lifecycle.StartedAsync(id, "run 3 started", dependencyRun: "3");
        await lifecycle.FinishAsync(id, ECommandOutcome.Done, "1 group applied");

        reactions.On(Comment).Should().Equal([EForgeReaction.Hooray]);
        reactions.Removed.Should().HaveCount(2, "👀 and 🚀 each made way for the next");
        var record = await CommandTestKit.InvocationAsync(store, Repo, Comment);
        record!.Outcome.Should().Be(ECommandOutcome.Done);
        record.DependencyRun.Should().Be("3");
        record.ExpiresAtUtc.Should().BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromMinutes(5));
        logs.Lines.Select(l => l.Message).Should().Equal(["/refresh by @maintainer: Started: run 3 started", "/refresh by @maintainer: Done: 1 group applied"]);
    }

    [Theory]
    [InlineData(ECommandOutcome.Done, EForgeReaction.Hooray)]
    [InlineData(ECommandOutcome.NoChange, EForgeReaction.ThumbsUp)]
    [InlineData(ECommandOutcome.Refused, EForgeReaction.ThumbsDown)]
    [InlineData(ECommandOutcome.Failed, EForgeReaction.ThumbsDown)]
    public async Task Every_outcome_has_its_reaction(ECommandOutcome outcome, EForgeReaction reaction)
    {
        var (_, lifecycle) = await LifecycleAsync();
        var id = await lifecycle.BeginAsync(Repo, 7, Comment, "/hold x", "maintainer");

        await lifecycle.FinishAsync(id, outcome, "why");

        reactions.On(Comment).Should().Equal([reaction]);
    }

    [Fact]
    public async Task A_started_command_ends_only_for_its_own_run()
    {
        var (store, lifecycle) = await LifecycleAsync();
        var id = await lifecycle.BeginAsync(Repo, 7, Comment, "/queue", "maintainer");
        await lifecycle.StartedAsync(id, "queued", mergeQueueRun: "run-1");

        await lifecycle.FinishStartedAsync(id, r => r.MergeQueueRun == "run-2", ECommandOutcome.Done, "a later run merged");
        reactions.On(Comment).Should().Equal([EForgeReaction.Rocket], "G3: an old /queue never takes a later run's 🎉");

        await lifecycle.FinishStartedAsync(id, r => r.MergeQueueRun == "run-1", ECommandOutcome.Done, "merged");
        reactions.On(Comment).Should().Equal([EForgeReaction.Hooray]);

        await lifecycle.FinishStartedAsync(id, r => r.MergeQueueRun == "run-1", ECommandOutcome.Failed, "late duplicate");
        reactions.On(Comment).Should().Equal([EForgeReaction.Hooray], "a finished command is not started any more");
        (await CommandTestKit.InvocationAsync(store, Repo, Comment))!.Outcome.Should().Be(ECommandOutcome.Done);
    }

    [Fact]
    public async Task An_end_without_a_reaction_keeps_the_rocket_but_ends_the_record()
    {
        var (store, lifecycle) = await LifecycleAsync();
        var id = await lifecycle.BeginAsync(Repo, 7, Comment, "/queue", "maintainer");
        await lifecycle.StartedAsync(id, "queued", mergeQueueRun: "run-1");

        await lifecycle.FinishStartedAsync(id, _ => true, ECommandOutcome.NoChange, "dequeued", react: false);

        reactions.On(Comment).Should().Equal([EForgeReaction.Rocket]);
        var record = await CommandTestKit.InvocationAsync(store, Repo, Comment);
        record!.Outcome.Should().Be(ECommandOutcome.NoChange);
        record.ExpiresAtUtc.HasValue.Should().BeTrue();
    }

    [Fact]
    public async Task A_forge_that_refuses_reactions_never_fails_the_command()
    {
        var (store, lifecycle) = await LifecycleAsync();
        reactions.Fault = new ForgeAccessDeniedException("issues: write");

        var id = await lifecycle.BeginAsync(Repo, 7, Comment, "/approve x", "maintainer");
        await lifecycle.FinishAsync(id, ECommandOutcome.Done, "approved");

        id.Should().NotBeNull("R7: the record stands although the reaction failed");
        var record = await CommandTestKit.InvocationAsync(store, Repo, Comment);
        record!.Outcome.Should().Be(ECommandOutcome.Done);
        record.CurrentReaction.HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task A_linked_command_writes_its_steps_to_the_engines_log()
    {
        var (_, lifecycle) = await LifecycleAsync();
        var id = await lifecycle.BeginAsync(Repo, 7, Comment, "/queue", "maintainer");

        await lifecycle.LinkLogAsync(id, LogIds.ForKey("merge-queue/github/6600/7/run-1"));
        await lifecycle.FinishAsync(id, ECommandOutcome.Refused, "not mergeable");

        logs.Lines.Should().ContainSingle().Which.Key.Should().Be("merge-queue/github/6600/7/run-1");
        logs.Opened.Last().Related.Should().BeNull("the engine's log keeps its own relation");
    }

    [Fact]
    public void Without_a_command_a_log_of_its_own_is_opened_with_the_retention()
    {
        var lifecycle = CommandTestKit.Lifecycle(null!, reactions, logs: logs, retentionDays: 30);

        using var log = lifecycle.OpenLog(null, "backports/github/6600/7/1", "Repositories/github/6600");

        log.Id.Should().Be(LogIds.ForKey("backports/github/6600/7/1"));
        var opened = logs.Opened.Should().ContainSingle().Which;
        opened.Related.Should().Be("Repositories/github/6600");
        (opened.ExpiresAtUtc > DateTime.UtcNow.AddDays(29)).Should().BeTrue("G11: a log of its own expires after the retention");
    }
}
