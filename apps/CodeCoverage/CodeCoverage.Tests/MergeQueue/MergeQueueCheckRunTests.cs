using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using Xunit;
using static CodeCoverage.Tests.MergeQueue.MergeQueueHarness;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// The queue's own check run when the pull request's head moves under a running entry (merge-queue PRD §0 D92). The run
/// on the commit left behind is completed, neutral, "Superseded: continued on …": left in progress, it stayed pending on
/// that commit for good, which a squash-merged pull request keeps listing. A verdict already given stays as it is.
/// </summary>
public class MergeQueueCheckRunTests : CoverageRavenTest
{
    private const string Pushed = "9999999999999999999999999999999999999999";

    private async Task<MergeQueueHarness> HarnessAsync(string mergeBase = OldBase)
    {
        var harness = new MergeQueueHarness(GetDocumentStore(), mergeBase);
        await harness.SeedAsync();
        return harness;
    }

    /// <summary>The first run is the first check run the fake forge creates: id 100 (the lane is idle, so it starts at once).</summary>
    private const long FirstRun = 100;

    private static bool IsSuperseded(ForgeQueueCheck check) => check.Title.StartsWith("Superseded", StringComparison.Ordinal);

    [Fact]
    public async Task A_publish_that_moves_the_head_completes_the_run_it_leaves_behind()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        var (_, left, existing) = h.Queue.QueueChecks.Last(c => c.Sha == Head);
        existing.Should().Be(FirstRun, "the run on the old head is completed through its own id");
        left.State.Should().Be(EForgeQueueCheckState.Neutral);
        left.Title.Should().Be("Superseded: continued on 5555555");
        left.Summary.Should().Contain(Final);
        left.Actions.Should().BeEmpty();
        h.Queue.QueueChecks.First(c => c.Sha == Final).ExistingId.HasValue.Should().BeFalse("the new head gets a new run");
        h.Queue.QueueChecks.Last(c => c.Sha == Final).Check.State.Should().Be(EForgeQueueCheckState.Success);
        h.Queue.QueueChecks.FindIndex(c => c.Sha == Head && IsSuperseded(c.Check))
            .Should().BeLessThan(h.Queue.QueueChecks.FindIndex(c => c.Sha == Final), "the old run is closed before the new one starts");
    }

    /// <summary>Case 1 merges the head as it is: the run never moves, so it is updated in place and nothing is superseded.</summary>
    [Fact]
    public async Task A_run_that_stays_on_its_head_is_updated_in_place()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        h.Queue.QueueChecks.Should().OnlyContain(c => c.Sha == Head);
        h.Queue.QueueChecks[0].ExistingId.HasValue.Should().BeFalse();
        h.Queue.QueueChecks.Skip(1).Should().OnlyContain(c => c.ExistingId == FirstRun);
        h.Queue.QueueChecks.Should().NotContain(c => IsSuperseded(c.Check));
    }

    /// <summary>
    /// A base that moves after the publish sends the run back to prepare on the head it has (in place), and its next publish
    /// moves the head again: the run on the first published commit is superseded in turn.
    /// </summary>
    [Fact]
    public async Task A_restart_keeps_its_head_and_its_next_publish_supersedes_the_run_on_the_first_one()
    {
        const string prepared2 = "7777777777777777777777777777777777777777";
        const string final2 = "8888888888888888888888888888888888888888";
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));
        h.Queue.CheckStates[Final] = Pending(Final);
        await h.EnqueueAsync();
        await h.DrainAsync();
        var finalRun = (await h.EntryAsync()).QueueCheckId!.Value;

        h.Queue.BranchHeads["master"] = NewBase;
        h.Forge.PullRequests[Number] = PullRequest(head: Final);
        await h.SignalAsync(EMergeQueueSignal.BranchPushed, sha: NewBase, branch: "master");

        var restarted = h.Queue.QueueChecks[^1];
        restarted.Sha.Should().Be(Final);
        restarted.ExistingId.Should().Be(finalRun, "a restart keeps the head, so its run is updated in place");
        restarted.Check.State.Should().Be(EForgeQueueCheckState.InProgress);

        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(NewBase, Final, prepared2));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(final2));
        h.Queue.CheckStates[final2] = Green(final2);
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        var (_, left, existing) = h.Queue.QueueChecks.Last(c => c.Sha == Final);
        existing.Should().Be(finalRun);
        left.Title.Should().Be("Superseded: continued on 8888888");
        left.State.Should().Be(EForgeQueueCheckState.Neutral);
        h.Queue.QueueChecks.Last(c => c.Sha == Head).Check.Title.Should().Be("Superseded: continued on 5555555");
        h.Queue.QueueChecks.Last(c => c.Sha == final2).Check.State.Should().Be(EForgeQueueCheckState.Success);
    }

    /// <summary>
    /// A push the queue did not make ends the run red on the head it was on (owner, 2026-10-15), and that verdict stays: the
    /// new head gets the "Not queued" offer, not the run, so nothing supersedes the red one.
    /// </summary>
    [Fact]
    public async Task An_author_push_evictions_red_run_is_not_superseded()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));
        h.Queue.CheckStates[Final] = Pending(Final);
        await h.EnqueueAsync();
        await h.DrainAsync();
        h.Forge.PullRequests[Number] = PullRequest(head: Pushed);

        await h.SignalAsync(EMergeQueueSignal.HeadPushed, Number, Pushed);

        (await h.EntryAsync()).EvictionReason.Should().Be("author-push");
        var red = h.Queue.QueueChecks.Last(c => c.Sha == Final).Check;
        red.State.Should().Be(EForgeQueueCheckState.Failure);
        red.Title.Should().Be("Evicted: author-push");
        h.Queue.QueueChecks.Where(c => c.Sha == Final).Should().NotContain(c => IsSuperseded(c.Check));
        h.Queue.QueueChecks.Last(c => c.Sha == Pushed).Check.Title.Should().Be(MergeQueueCheckRenderer.NotQueuedTitle);
    }
}
