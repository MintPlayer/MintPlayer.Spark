using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Tests.Commands;
using Raven.Client.Documents;
using Xunit;
using static CodeCoverage.Tests.MergeQueue.MergeQueueHarness;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// #660 G3 for the merge queue: what a <c>/queue</c> or <c>/dequeue</c> comment shows through the run it started, and
/// that a label (no comment) still gets a run log.
/// </summary>
public class MergeQueueCommandReactionTests : CoverageRavenTest
{
    private const long QueueComment = 5001;
    private const long DequeueComment = 5002;

    private static readonly Repository Repo = new() { Provider = EForgeProvider.GitHub, GitHubId = RepoGitHubId };

    private async Task<(MergeQueueHarness, IDocumentStore)> HarnessAsync(string mergeBase = OldBase)
    {
        var store = GetDocumentStore();
        var harness = new MergeQueueHarness(store, mergeBase);
        await harness.SeedAsync();
        return (harness, store);
    }

    [Fact]
    public async Task A_queue_comment_shows_rocket_while_its_run_goes_and_hooray_when_it_merges()
    {
        var (h, store) = await HarnessAsync(mergeBase: BaseSha);

        await h.EnqueueAsync(source: EMergeQueueCommandSource.Comment, label: null, commentId: QueueComment);
        h.Reactions.On(QueueComment).Should().Equal([EForgeReaction.Rocket], "the run is queued, not finished");
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        h.Reactions.On(QueueComment).Should().Equal([EForgeReaction.Hooray]);
        h.Reactions.Added.Select(a => a.Reaction).Should().Equal([EForgeReaction.Eyes, EForgeReaction.Rocket, EForgeReaction.Hooray]);
        var record = await CommandTestKit.InvocationAsync(store, Repo, QueueComment);
        record!.Outcome.Should().Be(ECommandOutcome.Done);
        record.MergeQueueRun.Should().NotBeNull();
        record.LogId.Should().Be((await h.EntryAsync()).LogId, "G7: the command's log is the run's log");
        record.ExpiresAtUtc.HasValue.Should().BeTrue("G5: a finished command expires");
    }

    [Fact]
    public async Task A_dequeue_comment_gets_hooray_and_the_queue_comment_keeps_its_rocket()
    {
        var (h, store) = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));
        h.Queue.CheckStates[Final] = Pending(Final);
        await h.EnqueueAsync(source: EMergeQueueCommandSource.Comment, label: null, commentId: QueueComment);
        await h.DrainAsync();

        await h.DequeueAsync("maintainer", commentId: DequeueComment);
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Dequeued);
        h.Reactions.On(DequeueComment).Should().Equal([EForgeReaction.Hooray]);
        h.Reactions.On(QueueComment).Should().Equal([EForgeReaction.Rocket], "G3: a dequeued /queue gets no final reaction");
        (await CommandTestKit.InvocationAsync(store, Repo, QueueComment))!.Outcome.Should().Be(ECommandOutcome.NoChange, "the record still ends, and expires");
    }

    [Fact]
    public async Task A_queue_comment_by_someone_without_write_access_gets_thumbs_down()
    {
        var (h, store) = await HarnessAsync();
        h.Queue.Roles["visitor"] = EForgeRepositoryRole.Read;

        await h.EnqueueAsync("visitor", EMergeQueueCommandSource.Comment, label: null, commentId: QueueComment);
        await h.DrainAsync();

        h.Reactions.On(QueueComment).Should().Equal([EForgeReaction.ThumbsDown]);
        var record = await CommandTestKit.InvocationAsync(store, Repo, QueueComment);
        record!.Outcome.Should().BeOneOf(ECommandOutcome.Refused, ECommandOutcome.Failed);
    }

    [Fact]
    public async Task A_dequeue_comment_on_a_pull_request_that_is_not_queued_gets_thumbs_up()
    {
        var (h, store) = await HarnessAsync();

        await h.DequeueAsync("maintainer", commentId: DequeueComment);

        h.Reactions.On(DequeueComment).Should().Equal([EForgeReaction.ThumbsUp]);
        (await CommandTestKit.InvocationAsync(store, Repo, DequeueComment))!.Outcome.Should().Be(ECommandOutcome.NoChange);
    }

    [Fact]
    public async Task A_second_queue_comment_on_a_queued_pull_request_gets_thumbs_up_and_the_first_keeps_its_run()
    {
        var (h, _) = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));
        h.Queue.CheckStates[Final] = Pending(Final);
        await h.EnqueueAsync(source: EMergeQueueCommandSource.Comment, label: null, commentId: QueueComment);
        await h.DrainAsync();

        await h.EnqueueAsync(source: EMergeQueueCommandSource.Comment, label: null, commentId: QueueComment + 10);

        h.Reactions.On(QueueComment + 10).Should().Equal([EForgeReaction.ThumbsUp]);
        h.Reactions.On(QueueComment).Should().Equal([EForgeReaction.Rocket]);
    }

    [Fact]
    public async Task A_replayed_queue_comment_reacts_and_records_once()
    {
        var (h, store) = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));
        h.Queue.CheckStates[Final] = Pending(Final);

        await h.EnqueueAsync(source: EMergeQueueCommandSource.Comment, label: null, commentId: QueueComment);
        await h.EnqueueAsync(source: EMergeQueueCommandSource.Comment, label: null, commentId: QueueComment);

        h.Reactions.On(QueueComment).Should().Equal([EForgeReaction.Rocket], "the redelivery is the same command, not a second one");
        h.Reactions.Added.Count(a => a.Reaction == EForgeReaction.Eyes).Should().Be(1);
        (await CommandTestKit.InvocationAsync(store, Repo, QueueComment))!.Outcome.Should().Be(ECommandOutcome.Started);
    }

    [Fact]
    public async Task A_label_has_no_reaction_but_its_run_gets_a_log()
    {
        var (h, _) = await HarnessAsync(mergeBase: BaseSha);

        await h.EnqueueAsync();
        await h.DrainAsync();

        h.Reactions.Added.Should().BeEmpty();
        var entry = await h.EntryAsync();
        entry.LogId.Should().StartWith("Logs/merge-queue/github/910040/7/", "G11: a label's run is logged too");
        h.Logs.Opened.Should().Contain(o => o.Key == entry.LogId![5..] && o.Related == RepositoryId && o.ExpiresAtUtc != null,
            "G15: a run log opens with its repository (a merge-queue entry is no persistent object)");
    }
}
