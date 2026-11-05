using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using Xunit;
using static CodeCoverage.Tests.MergeQueue.MergeQueueHarness;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// Every answer of the git writer at each of the saga's three jobs (merge-queue PRD §5.1, §0 D57, D65): a head or base that
/// moved, a refused push, a refused patch and an answer the saga does not know. Each ends the entry, restarts it, or is a
/// transient failure that is retried, never a silent stop.
/// </summary>
public class MergeQueueWriterOutcomeTests : CoverageRavenTest
{
    private const string Regenerated = "apps/A/App_Data/modelHashes.json";

    private async Task<MergeQueueHarness> HarnessAsync()
    {
        var harness = new MergeQueueHarness(GetDocumentStore(), OldBase);
        await harness.SeedAsync();
        return harness;
    }

    /// <summary>The second prepare of a restarted entry: evicted, so the run stops there and the test can count prepares.</summary>
    private static GitWriterPrepareResult StopsTheRestart()
        => new(GitWriterOutcomes.Evicted, "conflict-outside-allow-list", BaseSha, Head, null, [], ["src/Program.cs"], "conflict", 5);

    private static async Task<MergeQueueEntry> RunAsync(MergeQueueHarness h)
    {
        await h.EnqueueAsync();
        await h.DrainAsync();
        return await h.EntryAsync();
    }

    // ── prepare ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_head_that_moved_before_the_prepare_evicts_as_an_author_push()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(new GitWriterPrepareResult(GitWriterOutcomes.HeadMismatch, null, BaseSha, Head, null, [], [], "head moved", 5));

        var entry = await RunAsync(h);

        (entry.State, entry.EvictionReason).Should().Be((EMergeQueueEntryState.Evicted, "author-push"));
        entry.EvictionMessage.Should().Contain("no longer the queued commit").And.Contain("Queue it again");
    }

    [Fact]
    public async Task A_base_that_moved_during_the_prepare_rebases_again()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(new GitWriterPrepareResult(GitWriterOutcomes.BaseMoved, null, BaseSha, Head, null, [], [], "base moved", 5));
        h.Writer.PrepareResults.Enqueue(StopsTheRestart());

        var entry = await RunAsync(h);

        h.Writer.Prepares.Should().HaveCount(2, "a moved base is the queue's to absorb, not the pull request's fault");
        entry.EvictionReason.Should().Be("conflict-outside-allow-list");
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("queue-branch")]
    [InlineData("publish")]
    public async Task An_answer_the_saga_does_not_know_is_a_transient_failure(string job)
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(job == "prepare"
            ? new GitWriterPrepareResult("from-the-future", null, BaseSha, Head, null, [], [], "?", 5)
            : FakeGitWriter.Prepared(BaseSha, Head, Prepared, job == "queue-branch" ? [Regenerated] : []));
        if (job == "queue-branch") h.Writer.QueueBranchResults.Enqueue(new GitWriterQueueBranchResult("from-the-future", null, null, [], "?", 5));
        if (job == "publish") h.Writer.PublishResults.Enqueue(new GitWriterPublishResult("from-the-future", null, null, [], "?", 5));

        var entry = await RunAsync(h);

        entry.TransientFailures.Should().Be(1);
        entry.State.Should().NotBe(EMergeQueueEntryState.Evicted, "one unknown answer is retried, not the end of the entry");
    }

    // ── queue-branch push ────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string, string, string> QueueBranchEvictions => new()
    {
        { GitWriterOutcomes.HeadMismatch, "author-push", "no longer the queued commit" },
        { GitWriterOutcomes.PushRejected, "push-rejected", "The forge refused the push to `mq/master/pr-7`: denied" },
    };

    [Theory]
    [MemberData(nameof(QueueBranchEvictions))]
    public async Task A_queue_branch_push_the_writer_refuses_evicts_without_dispatching(string outcome, string reason, string message)
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, Regenerated));
        h.Writer.QueueBranchResults.Enqueue(new GitWriterQueueBranchResult(outcome, null, null, [], "denied", 5));

        var entry = await RunAsync(h);

        (entry.State, entry.EvictionReason).Should().Be((EMergeQueueEntryState.Evicted, reason));
        entry.EvictionMessage.Should().Contain(message);
        h.Queue.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task A_queue_branch_push_the_writer_evicts_carries_its_reason_and_paths()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, Regenerated));
        h.Writer.QueueBranchResults.Enqueue(new GitWriterQueueBranchResult(GitWriterOutcomes.Evicted, "conflict-outside-allow-list", null, ["src/Program.cs"], "conflict", 5));

        var entry = await RunAsync(h);

        (entry.State, entry.EvictionReason).Should().Be((EMergeQueueEntryState.Evicted, "conflict-outside-allow-list"));
        entry.EvictionPaths.Should().Equal(["src/Program.cs"]);
        h.Queue.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task A_base_gone_from_under_the_queue_branch_push_rebases_again()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, Regenerated));
        h.Writer.QueueBranchResults.Enqueue(new GitWriterQueueBranchResult(GitWriterOutcomes.BaseMoved, null, null, [], "base gone", 5));
        h.Writer.PrepareResults.Enqueue(StopsTheRestart());

        await RunAsync(h);

        h.Writer.Prepares.Should().HaveCount(2);
        h.Queue.Dispatches.Should().BeEmpty();
    }

    /// <summary>O35 #4: the writer's resolver changed between the prepare and the push; that is not the pull request's fault.</summary>
    [Fact]
    public async Task A_queue_branch_rebuild_that_differs_because_the_resolver_changed_rebases_again()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, Regenerated) with { ResolverVersion = "resolver-1" });
        h.Writer.QueueBranchResults.Enqueue(new GitWriterQueueBranchResult(GitWriterOutcomes.PreparedDiffers, null, null, [], "rebuilt another commit", 5)
        {
            ResolverVersion = "resolver-2",
        });
        h.Writer.PrepareResults.Enqueue(StopsTheRestart());

        await RunAsync(h);

        h.Writer.Prepares.Should().HaveCount(2);
    }

    // ── cleanup ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A dispatched run that merges: prepare, queue-branch push, publish, then the queue branch's cleanup.</summary>
    private static async Task MergeThroughTheQueueBranchAsync(MergeQueueHarness h)
    {
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, Regenerated));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "completed", "success", Prepared, "mq/master/pr-7", null);
        h.Queue.Artifacts[9001] = new ForgeArtifactDownload(EForgeArtifactOutcome.Ok, "patch.diff", [], null);
        await h.EnqueueAsync();
        await h.DrainAsync();
        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9001);
    }

    /// <summary>§0 D57: a refusal is the same on every retry; the branch is left for a person, the step does not fail.</summary>
    [Fact]
    public async Task A_cleanup_the_writer_refuses_leaves_the_branch_without_failing_the_step()
    {
        var h = await HarnessAsync();
        h.Writer.DeleteAnswer = _ => throw new GitWriterRefusedException("queue-branch", "queueBranch", "a queue branch");
        await MergeThroughTheQueueBranchAsync(h);

        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        h.Writer.Deletes.Should().ContainSingle();
    }

    /// <summary>A busy or failed writer is worth another try: the step throws into the messaging runtime's retries.</summary>
    [Theory]
    [InlineData(GitWriterOutcomes.Busy)]
    [InlineData(GitWriterOutcomes.Failed)]
    public async Task A_cleanup_the_writer_could_not_run_is_retried(string outcome)
    {
        var h = await HarnessAsync();
        h.Writer.DeleteAnswer = _ => new GitWriterDeleteResult(outcome, null, "all slots taken", 1);
        await MergeThroughTheQueueBranchAsync(h);

        await ((Func<Task>)h.DrainAsync).Should().ThrowAsync<GitWriterUnavailableException>().WithMessage($"*delete with {outcome}*");

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged, "the entry ended; only its branch is left");
    }

    // ── publish ──────────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string?, string> PatchRefusals => new()
    {
        { GitWriterReasons.InvalidContent, "not valid for its type" },
        { "outside-regenerated-paths", "may only touch `regeneratedPaths`" },
    };

    [Theory]
    [MemberData(nameof(PatchRefusals))]
    public async Task A_regenerate_patch_the_writer_refuses_evicts_naming_why(string? reason, string message)
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(new GitWriterPublishResult(GitWriterOutcomes.PatchRejected, reason, null, [Regenerated], "line 3", 5));

        var entry = await RunAsync(h);

        (entry.State, entry.EvictionReason).Should().Be((EMergeQueueEntryState.Evicted, "patch-rejected"));
        entry.EvictionMessage.Should().Contain(message).And.Contain("line 3");
        entry.EvictionPaths.Should().Equal([Regenerated]);
        h.Queue.MergeRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_publish_the_forge_refuses_on_the_base_repository_evicts_with_the_forges_reason()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(new GitWriterPublishResult(GitWriterOutcomes.PushRejected, null, null, [], "protected branch", 5));

        var entry = await RunAsync(h);

        (entry.State, entry.EvictionReason).Should().Be((EMergeQueueEntryState.Evicted, "push-rejected"));
        entry.EvictionMessage.Should().Be("The forge refused the push to `feature-7`: protected branch");
    }
}
