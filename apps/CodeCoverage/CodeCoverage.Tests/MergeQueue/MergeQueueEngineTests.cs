using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.MergeQueue;
using CodeCoverage.Tests.Services;
using Xunit;
using static CodeCoverage.Tests.MergeQueue.MergeQueueHarness;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// The saga's state machine (merge-queue PRD M5, §5.1, O20), with the forge and the git writer scripted and the queue
/// state in a real RavenDB. Each test names the PRD decision or threat it holds.
/// </summary>
public class MergeQueueEngineTests : CoverageRavenTest
{
    private async Task<MergeQueueHarness> HarnessAsync(string mergeBase = OldBase)
    {
        var harness = new MergeQueueHarness(GetDocumentStore(), mergeBase);
        await harness.SeedAsync();
        return harness;
    }

    // ── The three cases of O20 ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case_1_a_head_that_contains_the_latest_base_and_is_green_merges_directly()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.DirectMerge.Should().BeTrue();
        entry.FinalSha.Should().Be(Head);
        entry.MergeCommitSha.Should().Be(MergeCommit);
        h.Writer.Prepares.Should().BeEmpty("nothing is rebased when the head already contains the base");
        h.Writer.Publishes.Should().BeEmpty();
        h.Queue.MergeRequests.Should().Equal([(Number, Head)]);
        h.Queue.LastCheck.State.Should().Be(EForgeQueueCheckState.Success);
        h.Queue.RemovedLabels.Should().Contain((Number, "merge-queue"));
        (await h.QueueDocumentAsync())!.IsBusy().Should().BeFalse();
    }

    [Fact]
    public async Task Case_2_a_clean_rebase_is_published_checked_and_merged()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.KnownShas.Should().Equal([Head, Prepared, Final]);

        var prepare = h.Writer.Prepares.Should().ContainSingle().Which;
        prepare.HeadSha.Should().Be(Head, "prepare fetches exactly the pinned head (T3)");
        prepare.HeadRef.Should().Be("refs/pull/7/head");
        prepare.BaseSha.Should().Be(BaseSha);
        prepare.QueueBranch.Should().Be("mq/master/pr-7");
        prepare.ExpectedQueueSha.Should().BeNull();
        prepare.Message.Should().Be("Add widgets (#7)");
        prepare.AutoResolve.Should().Equal([new GitWriterAutoResolve("**/App_Data/modelHashes.json", AnyLine: true)]);

        var publish = h.Writer.Publishes.Should().ContainSingle().Which;
        publish.Patch.Should().BeNull("nothing was resolved, so nothing was regenerated");
        publish.ExpectedPrHeadSha.Should().Be(Head, "the lease is the pinned head");
        publish.PrBranch.Should().Be("feature-7");
        publish.PushUrl.Should().BeNull();
        h.Queue.Dispatches.Should().BeEmpty();
        h.Queue.MergeRequests.Should().Equal([(Number, Final)]);

        // D5: the queue branch is deleted under a lease on the commit the run left there.
        h.Writer.Deletes.Should().ContainSingle().Which.ExpectedSha.Should().Be(Prepared);
    }

    [Fact]
    public async Task Case_3_a_resolved_rebase_is_regenerated_then_published_with_the_patch()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final, "apps/A/App_Data/modelHashes.json"));
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "completed", "success", Prepared, "mq/master/pr-7", "https://github.com/acme/widget/actions/runs/9001");
        h.Queue.Artifacts[9001] = new ForgeArtifactDownload(EForgeArtifactOutcome.Ok, "patch.diff", "diff --git a/x b/x\n"u8.ToArray(), null);

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Regenerating);
        var dispatch = h.Queue.Dispatches.Should().ContainSingle().Which;
        dispatch.Workflow.Should().Be("regenerate.yml");
        dispatch.Branch.Should().Be("mq/master/pr-7");
        dispatch.Inputs["expected_sha"].Should().Be(Prepared);
        dispatch.Inputs["pr_number"].Should().Be("7");

        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9001, sha: Prepared);
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.DispatchRunId.Should().Be(9001);
        h.Writer.Publishes.Should().ContainSingle().Which.Patch.Should().Be(Convert.ToBase64String("diff --git a/x b/x\n"u8.ToArray()));
        h.Queue.CancelledRuns.Should().BeEmpty("the run ended the wait itself (§0 D53)");
    }

    [Fact]
    public async Task A_green_run_with_an_empty_patch_publishes_the_prepared_commit_unchanged()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Prepared));
        h.Queue.CheckStates[Prepared] = Green(Prepared);
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "completed", "success", Prepared, "mq/master/pr-7", null);
        h.Queue.Artifacts[9001] = new ForgeArtifactDownload(EForgeArtifactOutcome.Ok, "patch.diff", [], null);

        await h.EnqueueAsync();
        await h.DrainAsync();
        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9001);
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        h.Writer.Publishes.Should().ContainSingle().Which.Patch.Should().BeNull("an empty patch means nothing to regenerate (D35)");
    }

    // ── Evictions ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_conflict_in_a_hand_authored_file_evicts_and_the_next_entry_starts()
    {
        var h = await HarnessAsync();
        h.Forge.PullRequests[8] = PullRequest(8, "6666666666666666666666666666666666666666");
        h.Writer.PrepareResults.Enqueue(new GitWriterPrepareResult(GitWriterOutcomes.Evicted, "conflict-outside-allow-list", BaseSha, Head, null, [],
            ["apps/A/App_Data/translations.json"], "translations.json conflicts", 10));

        await h.EnqueueAsync();
        await h.EnqueueAsync(number: 8);
        (await h.EntryAsync(8)).State.Should().Be(EMergeQueueEntryState.Queued);

        await h.StepOnceAsync();

        var evicted = await h.EntryAsync();
        evicted.State.Should().Be(EMergeQueueEntryState.Evicted);
        evicted.EvictionReason.Should().Be("conflict-outside-allow-list");
        evicted.EvictionPaths.Should().Equal(["apps/A/App_Data/translations.json"]);
        h.Queue.Comments.Should().ContainSingle().Which.Body.Should().Contain("conflicts in files the queue may not resolve");
        h.Queue.RemovedLabels.Should().Contain((Number, "merge-queue"));
        h.Queue.QueueChecks.Last(c => c.Sha == Head).Check.State.Should().Be(EForgeQueueCheckState.Failure);

        (await h.EntryAsync(8)).State.Should().Be(EMergeQueueEntryState.Preparing, "D11: the lane moves on to the next entry");
        (await h.QueueDocumentAsync())!.LaneFor("master").Current.Should().Be(MergeQueueEntry.DocumentId(RepositoryId, 8));
    }

    [Fact]
    public async Task An_author_push_mid_run_evicts_while_the_queues_own_push_is_ignored()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));
        h.Queue.CheckStates[Final] = Pending(Final);
        await h.EnqueueAsync();
        await h.DrainAsync();
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.AwaitingChecks);

        // §5.5: our own publish echoes back as a synchronize with after == finalSha.
        await h.SignalAsync(EMergeQueueSignal.HeadPushed, Number, Final);
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.AwaitingChecks);

        await h.SignalAsync(EMergeQueueSignal.HeadPushed, Number, "9999999999999999999999999999999999999999");

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("author-push");
        h.Queue.MergeRequests.Should().BeEmpty();
    }

    // ── §0 D52 (D51 (1)): a publish whose answer never reached the saga ──────────────────────────────

    /// <summary>Prepared, then a publish that pushed but whose answer was lost (a dropped response; the app dying before it
    /// saved leaves the saga in the same place): the entry is still Publishing, and the queue's own commit is unknown to it.</summary>
    private async Task<MergeQueueHarness> PublishAnswerLostAsync()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishThrows = new HttpRequestException("The response ended prematurely.");
        h.Queue.CheckStates[Final] = Pending(Final);
        await h.EnqueueAsync();
        await h.DrainAsync();

        var lost = await h.EntryAsync();
        lost.State.Should().Be(EMergeQueueEntryState.Publishing);
        lost.KnownShas.Should().NotContain(Final);
        // GitHub now reports the queue's commit as the pull request's head.
        h.Forge.PullRequests[Number] = PullRequest(head: Final);
        return h;
    }

    [Fact]
    public async Task The_echo_of_a_publish_whose_answer_was_lost_is_recognised_not_evicted()
    {
        var h = await PublishAnswerLostAsync();
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final) with { Reason = GitWriterReasons.AlreadyPushed });

        // The synchronize for the queue's own push arrives before the publish is retried.
        await h.SignalAsync(EMergeQueueSignal.HeadPushed, Number, Final);

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.AwaitingChecks, "the queue's own commit is not an author push");
        entry.FinalSha.Should().Be(Final);
        entry.KnownShas.Should().Contain(Final);
        h.Writer.Publishes.Should().HaveCount(2, "the writer was asked whether the new head is the queue's own commit");

        // The retry that was already scheduled now belongs to a state the entry has left.
        await h.ReleaseAsync(EMergeQueueStep.Publish);
        h.Writer.Publishes.Should().HaveCount(2);
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.AwaitingChecks);
    }

    [Fact]
    public async Task A_retried_publish_that_meets_the_queues_own_commit_carries_on()
    {
        var h = await PublishAnswerLostAsync();
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final) with { Reason = GitWriterReasons.AlreadyPushed });

        // This time the retry runs first, and reads the queue's commit as the pull request's head.
        await h.ReleaseAsync(EMergeQueueStep.Publish);

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.AwaitingChecks);
        entry.History.Should().Contain(e => e.Text.Contains("an earlier attempt's push"));
        h.Writer.Publishes[1].ExpectedPrHeadSha.Should().Be(Head, "the lease is still the pinned head: the writer, not the saga, recognises the commit");

        await h.SignalAsync(EMergeQueueSignal.HeadPushed, Number, Final);
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.AwaitingChecks, "its echo is a known commit now");
        h.Queue.CheckStates[Final] = Green(Final);
        await h.SignalAsync(EMergeQueueSignal.ChecksChanged, sha: Final);
        await h.DrainAsync();
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        h.Queue.MergeRequests.Should().Equal([(Number, Final)]);
    }

    [Fact]
    public async Task An_author_push_while_publishing_evicts_once_the_writer_says_it_is_not_the_queues()
    {
        const string pushed = "9999999999999999999999999999999999999999";
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(new GitWriterPublishResult(GitWriterOutcomes.BranchMoved, null, null, [], $"refs/pull/7/head is at {pushed}, not {Head}.", 10));
        await h.EnqueueAsync();
        await h.StepOnceAsync();
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Publishing, "prepared; the publish step is queued");

        h.Forge.PullRequests[Number] = PullRequest(head: pushed);
        await h.SignalAsync(EMergeQueueSignal.HeadPushed, Number, pushed);

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("author-push");
        h.Writer.Publishes.Should().ContainSingle("the writer judged the new head before anything was evicted");
        await h.DrainAsync();
        h.Writer.Publishes.Should().ContainSingle("the queued publish step is stale");
        h.Queue.MergeRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_earlier_runs_cleanup_spares_the_queue_branch_the_next_run_adopted()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        await h.EnqueueAsync();
        await h.StepOnceAsync();
        await h.DequeueAsync("author");
        // Run 1's cleanup of mq/master/pr-7 (leased on Prepared) is held back, as a busy writer would delay it.
        var cleanup = h.Outbox.Messages.Select(m => m.Message).OfType<MergeQueueStepMessage>().Single(s => s.Step == EMergeQueueStep.Cleanup);
        cleanup.ExpectedSha.Should().Be(Prepared);
        h.Outbox.Messages.Clear();

        // Run 2 finds run 1's commit on the queue branch and adopts it.
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared) with { Reason = GitWriterReasons.AlreadyPushed });
        await h.EnqueueAsync();
        await h.StepOnceAsync();
        var adopted = await h.EntryAsync();
        adopted.Run.Should().Be(2);
        adopted.PreparedSha.Should().Be(Prepared);

        await h.StepAsync(cleanup);

        h.Writer.Deletes.Should().BeEmpty("the branch belongs to run 2 now");
    }

    // ── §0 D53 (D51 (5)): a regenerate run the entry stops waiting for is cancelled ──────────────────

    private async Task<MergeQueueHarness> RegeneratingAsync()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"));
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "in_progress", null, Prepared, "mq/master/pr-7", null);
        await h.EnqueueAsync();
        await h.DrainAsync();
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Regenerating);
        return h;
    }

    [Theory]
    [InlineData("base-moved")]
    [InlineData("author-push")]
    [InlineData("dequeued")]
    [InlineData("closed")]
    [InlineData("timeout")]
    public async Task A_regenerate_run_the_entry_stops_waiting_for_is_cancelled(string why)
    {
        const string prepared2 = "7777777777777777777777777777777777777777";
        var h = await RegeneratingAsync();

        switch (why)
        {
            case "base-moved":
                h.Queue.BranchHeads["master"] = NewBase;
                h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(NewBase, Head, prepared2, "apps/A/App_Data/modelHashes.json"));
                h.Queue.Blobs[$"{prepared2}:.github/workflows/regenerate.yml"] = "b10b";
                h.Queue.Runs[9002] = new ForgeWorkflowRun(9002, "in_progress", null, prepared2, "mq/master/pr-7", null);
                h.Queue.NextDispatch = new(EForgeDispatchOutcome.Dispatched, 9002, null, null);
                await h.SignalAsync(EMergeQueueSignal.BranchPushed, sha: NewBase, branch: "master");
                break;
            case "author-push":
                await h.SignalAsync(EMergeQueueSignal.HeadPushed, Number, "9999999999999999999999999999999999999999");
                break;
            case "dequeued":
                await h.DequeueAsync("author");
                break;
            case "closed":
                await h.SignalAsync(EMergeQueueSignal.Closed, Number);
                break;
            default:
                await h.ReleaseAsync(EMergeQueueStep.Timeout);
                break;
        }
        await h.DrainAsync();

        h.Queue.CancelledRuns.Should().Equal([9001L], $"after {why} nothing waits for run 9001 any more");
        var entry = await h.EntryAsync();
        entry.History.Should().Contain(e => e.Text == "cancelled the abandoned regenerate run 9001");
        if (why == "base-moved")
        {
            entry.State.Should().Be(EMergeQueueEntryState.Regenerating, "the rebase on the new base dispatched a run of its own");
            entry.DispatchRunId.Should().Be(9002);
        }
    }

    [Fact]
    public async Task A_run_started_by_a_dispatch_whose_answer_was_lost_is_cancelled_before_the_next_dispatch()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"));
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "in_progress", null, Prepared, "mq/master/pr-7", null);
        h.Queue.Runs[9002] = new ForgeWorkflowRun(9002, "in_progress", null, Prepared, "mq/master/pr-7", null);
        h.Queue.DispatchThrows = new HttpRequestException("The response ended prematurely.");
        await h.EnqueueAsync();
        await h.DrainAsync();

        var lost = await h.EntryAsync();
        lost.State.Should().Be(EMergeQueueEntryState.Preparing, "the dispatch is retried: its answer never arrived");
        lost.DispatchRunId.HasValue.Should().BeFalse("run 9001 started, but the entry never learnt its id");

        h.Queue.NextDispatch = new(EForgeDispatchOutcome.Dispatched, 9002, null, null);
        await h.ReleaseAsync(EMergeQueueStep.Dispatch);

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Regenerating);
        entry.DispatchRunId.Should().Be(9002);
        h.Queue.CancelledRuns.Should().Equal([9001L], "the stray run is cancelled before another one is paid for");
        entry.History.Should().Contain(e => e.Text.StartsWith("cancelled the stray regenerate run 9001", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_run_that_finished_by_itself_is_never_cancelled()
    {
        var h = await RegeneratingAsync();
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "completed", "failure", Prepared, "mq/master/pr-7", null);

        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9001);
        await h.DrainAsync();

        (await h.EntryAsync()).EvictionReason.Should().Be("regenerate-failed");
        h.Queue.CancelledRuns.Should().BeEmpty("its own completion ended the wait");
    }

    [Fact]
    public async Task A_cancel_refused_for_a_permission_is_noted_and_a_forge_failure_is_retried()
    {
        var h = await RegeneratingAsync();
        h.Queue.CancelThrows = new ForgeRequestException("GitHub answered 502 to POST …/cancel", 502);

        await h.DequeueAsync("author");
        var drain = () => h.DrainAsync();
        await drain.Should().ThrowAsync<ForgeRequestException>("the messaging runtime retries a step that fails like this");
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Dequeued, "the dequeue itself stands");

        h.Queue.CancelThrows = new ForgeAccessDeniedException("GitHub answered 403 to POST …/cancel: Resource not accessible by integration", "actions=write");
        await h.StepAsync(h.Handled[^1]);

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Dequeued);
        entry.History.Should().Contain(e => e.Text.StartsWith("could not cancel the abandoned regenerate run 9001", StringComparison.Ordinal));

        await h.StepAsync(h.Handled[^1]);
        h.Queue.CancelledRuns.Should().Equal([9001L], "a redelivered cancel step simply cancels");
    }

    [Fact]
    public async Task A_base_that_moves_while_checks_run_goes_back_to_prepare_not_out()
    {
        const string prepared2 = "7777777777777777777777777777777777777777";
        const string final2 = "8888888888888888888888888888888888888888";
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));
        h.Queue.CheckStates[Final] = Pending(Final);
        await h.EnqueueAsync();
        await h.DrainAsync();

        h.Queue.BranchHeads["master"] = NewBase;
        h.Forge.PullRequests[Number] = PullRequest(head: Final);
        await h.SignalAsync(EMergeQueueSignal.BranchPushed, sha: NewBase, branch: "master");

        var restarted = await h.EntryAsync();
        restarted.State.Should().Be(EMergeQueueEntryState.Preparing);
        restarted.PrepareAttempts.Should().Be(1);

        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(NewBase, Final, prepared2));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(final2));
        h.Queue.CheckStates[final2] = Green(final2);
        await h.DrainAsync();

        var again = h.Writer.Prepares[1];
        again.BaseSha.Should().Be(NewBase);
        again.HeadSha.Should().Be(Final, "the pull request's branch now holds the queue's earlier commit");
        again.ExpectedQueueSha.Should().Be(Prepared, "the queue branch is leased on the commit the run left there");
        h.Writer.Publishes[1].ExpectedPrHeadSha.Should().Be(Final);

        var merged = await h.EntryAsync();
        merged.State.Should().Be(EMergeQueueEntryState.Merged);
        h.Queue.MergeRequests.Should().Equal([(Number, final2)]);
    }

    [Fact]
    public async Task A_failed_regenerate_run_evicts()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"));
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "completed", "failure", Prepared, "mq/master/pr-7", "https://github.com/acme/widget/actions/runs/9001");
        await h.EnqueueAsync();
        await h.DrainAsync();

        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9001);

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("regenerate-failed");
        entry.EvictionMessage.Should().Contain("actions/runs/9001");
        h.Writer.Publishes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_green_run_without_its_artifact_evicts()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"));
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "completed", "success", Prepared, "mq/master/pr-7", null);
        await h.EnqueueAsync();
        await h.DrainAsync();

        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9001);
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("regenerate-artifact");
        h.Writer.Publishes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_that_built_another_commit_is_not_trusted()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"));
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "completed", "success", "abababababababababababababababababababab", "mq/master/pr-7", null);
        await h.EnqueueAsync();
        await h.DrainAsync();

        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9001);

        (await h.EntryAsync()).EvictionReason.Should().Be("regenerate-wrong-commit", "D14: only a run of the prepared commit counts");
    }

    [Fact]
    public async Task A_regenerate_run_that_never_finishes_times_out()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"));
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "in_progress", null, Prepared, "mq/master/pr-7", null);
        await h.EnqueueAsync();
        await h.DrainAsync();

        await h.ReleaseAsync(EMergeQueueStep.PollRun);
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Regenerating, "a poll of a running run waits");

        await h.ReleaseAsync(EMergeQueueStep.Timeout);
        (await h.EntryAsync()).EvictionReason.Should().Be("regenerate-timeout");
    }

    [Fact]
    public async Task A_workflow_file_that_differs_from_the_default_branch_is_never_dispatched()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"));
        h.Queue.Blobs[$"{Prepared}:.github/workflows/regenerate.yml"] = "e7e7";

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("workflow-differs", "D12: the run would execute the queue ref's copy");
        h.Queue.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task A_role_revoked_before_the_merge_evicts_without_merging()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));
        h.Queue.CheckStates[Final] = Pending(Final);
        await h.EnqueueAsync();
        await h.DrainAsync();

        h.Queue.Roles["maintainer"] = EForgeRepositoryRole.Read;
        h.Queue.CheckStates[Final] = Green(Final);
        await h.SignalAsync(EMergeQueueSignal.ChecksChanged, sha: Final);
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("role-revoked", "O9: the role is checked again right before the merge");
        h.Queue.MergeRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_merge_the_forge_refuses_for_a_rule_evicts_with_the_forges_words()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        var uuid = "d45ff22b-73cf-44a4-9eb0-cbf81c955578";
        h.Queue.MergeReplies.Enqueue(new ForgeMergeRequest(EForgeMergeState.Pending, uuid, null, "enqueued"));
        h.Queue.MergeStatuses.Enqueue(new ForgeMergeRequest(EForgeMergeState.Failed, null, null, "Repository rule violations found\n\nNew changes require approval from someone other than the last pusher."));

        await h.EnqueueAsync();
        await h.DrainAsync();
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merging, "D17: a 202 is not a merge");

        await h.ReleaseAsync(EMergeQueueStep.PollMerge);

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("merge-refused");
        entry.EvictionMessage.Should().Contain("someone other than the last pusher").And.Contain("O18");
    }

    [Fact]
    public async Task A_head_modified_answer_is_retried_once_after_a_delay()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Queue.MergeReplies.Enqueue(new ForgeMergeRequest(EForgeMergeState.HeadModified, null, null, "Head branch was modified. Review and try the merge again."));

        await h.EnqueueAsync();
        await h.DrainAsync();
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merging);

        await h.ReleaseAsync(EMergeQueueStep.Merge);

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.HeadModifiedRetries.Should().Be(1);
        h.Queue.MergeRequests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_second_head_modified_answer_evicts()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Queue.MergeReplies.Enqueue(new ForgeMergeRequest(EForgeMergeState.HeadModified, null, null, "modified"));
        h.Queue.MergeReplies.Enqueue(new ForgeMergeRequest(EForgeMergeState.HeadModified, null, null, "modified"));

        await h.EnqueueAsync();
        await h.DrainAsync();
        await h.ReleaseAsync(EMergeQueueStep.Merge);

        (await h.EntryAsync()).EvictionReason.Should().Be("head-modified");
    }

    [Fact]
    public async Task A_failed_check_is_rerun_once_then_evicts()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Queue.CheckStates[Head] = Red(Head);

        await h.EnqueueAsync();
        await h.DrainAsync();

        var waiting = await h.EntryAsync();
        waiting.State.Should().Be(EMergeQueueEntryState.AwaitingChecks, "D9: one re-run before evicting");
        waiting.CheckRetries.Should().Be(1);
        h.Queue.Reruns.Should().Equal([42L]);

        await h.ReleaseAsync(EMergeQueueStep.PollChecks);

        var entry = await h.EntryAsync();
        entry.EvictionReason.Should().Be("checks-failed");
        entry.EvictionMessage.Should().Contain("`build`");
    }

    [Fact]
    public async Task An_excluded_check_is_not_waited_for_and_is_listed_on_the_check_run()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        await SetAccountExclusionsAsync(h, "GitGuardian Security Checks");
        h.Queue.CheckStates[Head] = Green(Head) with { Statuses = [new ForgeCommitStatus("GitGuardian Security Checks", "failure", null)] };

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged, "O12: an excluded check does not hold the queue");
        entry.Config.CheckExclusions.Should().Equal(["GitGuardian Security Checks"]);
        var summary = h.Queue.LastCheck.Summary;
        summary.Should().Contain("`GitGuardian Security Checks`");
        summary.Should().Contain("| `checkExclusions` | GitGuardian Security Checks | organization |");
        summary.Should().Contain("coverage.yml @ master", "O15: every value names its source");
    }

    private static async Task SetAccountExclusionsAsync(MergeQueueHarness h, params string[] exclusions)
    {
        using var session = h.Store.OpenAsyncSession();
        var account = await session.LoadAsync<Account>(AccountId);
        account.MergeQueueCheckExclusions = [.. exclusions];
        await session.SaveChangesAsync();
    }

    // ── Enqueue: who and what (O3, O9, O11, D8, D16, O18, T1, T2) ───────────────────────────────────

    [Fact]
    public async Task A_label_by_a_triage_user_is_refused_and_removed()
    {
        var h = await HarnessAsync();
        h.Queue.Roles["triager"] = EForgeRepositoryRole.Triage;

        await h.EnqueueAsync("triager");

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Refused);
        entry.EvictionReason.Should().Be("role", "T2: triage cannot queue");
        h.Queue.RemovedLabels.Should().Equal([(Number, "merge-queue")]);
        h.Queue.LastCheck.Title.Should().Be("Not queued");
        h.Writer.Prepares.Should().BeEmpty();
        (await h.QueueDocumentAsync())!.IsBusy().Should().BeFalse();
    }

    [Fact]
    public async Task A_comment_command_acts_for_its_author_whatever_it_says()
    {
        var h = await HarnessAsync();

        // The normaliser passes the commenter as the actor (T1, MergeQueueWebhookTests); a fork author has no role.
        await h.EnqueueAsync("outsider", EMergeQueueCommandSource.Comment);

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Refused);
        entry.EnqueuedBy.Should().Be("outsider");
        h.Queue.Comments.Should().ContainSingle().Which.Body.Should().Contain("@outsider needs at least write access");
        h.Queue.RemovedLabels.Should().BeEmpty("a comment is answered with a comment");
    }

    [Fact]
    public async Task A_fork_without_maintainer_edits_is_refused()
    {
        var h = await HarnessAsync();
        h.Forge.PullRequests[Number] = PullRequest(fork: true, maintainerCanModify: false);

        await h.EnqueueAsync();

        var entry = await h.EntryAsync();
        entry.EvictionReason.Should().Be("fork");
        entry.EvictionMessage.Should().Contain("Allow edits by maintainers");
    }

    [Fact]
    public async Task A_fork_push_GitHub_refuses_names_the_open_question()
    {
        var h = await HarnessAsync();
        h.Forge.PullRequests[Number] = PullRequest(fork: true, maintainerCanModify: true);
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(new GitWriterPublishResult(GitWriterOutcomes.PushRejected, null, null, [], "! [remote rejected] feature-7 (permission denied)", 10));

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.EvictionReason.Should().Be("fork-push-rejected");
        entry.EvictionMessage.Should().Contain("S6");
        h.Writer.Publishes.Should().ContainSingle().Which.PushUrl.Should().Be("https://github.com/contributor/widget.git", "O3: the fork's branch");
    }

    [Fact]
    public async Task An_external_author_needs_an_approval_of_the_pinned_commit_by_a_writer()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Forge.PullRequests[Number] = PullRequest(author: "contributor");
        h.Queue.Reviews.Add(new ForgeReview(1, "maintainer", EForgeReviewState.Approved, "abababababababababababababababababababab", null));

        await h.EnqueueAsync();
        (await h.EntryAsync()).EvictionReason.Should().Be("approval", "O11: an approval of an older commit does not count");

        h.Queue.Reviews.Add(new ForgeReview(2, "maintainer", EForgeReviewState.Approved, Head, null));
        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.Run.Should().Be(2);
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.PastRuns.Should().ContainSingle().Which.State.Should().Be(EMergeQueueEntryState.Refused);
    }

    [Fact]
    public async Task A_later_change_request_withdraws_an_approval()
    {
        var h = await HarnessAsync();
        h.Forge.PullRequests[Number] = PullRequest(author: "contributor");
        h.Queue.Reviews.Add(new ForgeReview(1, "maintainer", EForgeReviewState.Approved, Head, null));
        h.Queue.Reviews.Add(new ForgeReview(2, "maintainer", EForgeReviewState.ChangesRequested, Head, null));

        await h.EnqueueAsync();

        (await h.EntryAsync()).EvictionReason.Should().Be("approval");
    }

    [Fact]
    public async Task The_repository_rules_floor_needs_no_approval()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Forge.PullRequests[Number] = PullRequest(author: "contributor");
        using (var session = h.Store.OpenAsyncSession())
        {
            var repository = await session.LoadAsync<Entities.Repository>(RepositoryId);
            repository.MergeQueueApprovalFloor = EMergeQueueApprovalFloorPolicy.RepositoryRules;
            await session.SaveChangesAsync();
        }

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.Config.Values.Should().Contain(v => v.Key == "approvalFloor" && v.Value == "RepositoryRules" && v.Source == "repository");
    }

    [Fact]
    public async Task A_branch_with_the_native_merge_queue_is_refused()
    {
        var h = await HarnessAsync();
        h.Queue.Rules = h.Queue.Rules with { HasNativeMergeQueue = true };

        await h.EnqueueAsync();

        (await h.EntryAsync()).EvictionReason.Should().Be("native-queue", "D8");
    }

    [Fact]
    public async Task A_ruleset_the_App_can_bypass_that_holds_the_reviews_is_refused_but_restrict_updates_may_exempt_it()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Queue.Rules = h.Queue.Rules with
        {
            Rulesets =
            [
                new ForgeRuleset(1, "Restrict updates", ["update"], EForgeRulesetBypass.Exempt),
                new ForgeRuleset(2, "Reviews and checks", ["pull_request", "required_status_checks"], EForgeRulesetBypass.Exempt),
            ],
        };

        await h.EnqueueAsync();
        (await h.EntryAsync()).EvictionReason.Should().Be("bypass", "T7/D16: the bot must be held to ruleset 2");

        h.Queue.Rules = h.Queue.Rules with
        {
            Rulesets =
            [
                new ForgeRuleset(1, "Restrict updates", ["update"], EForgeRulesetBypass.Exempt),
                new ForgeRuleset(2, "Reviews and checks", ["pull_request", "required_status_checks"], EForgeRulesetBypass.Never),
            ],
            RequiredChecks = ["build"],
            DismissStaleReviewsOnPush = true,
        };
        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.RequiredChecks.Should().Equal(["build"]);
        entry.Warnings.Should().ContainSingle().Which.Should().Contain("dismisses stale approvals", "O18 warns, it does not refuse");
    }

    [Fact]
    public async Task A_broken_configuration_is_refused_with_its_errors()
    {
        var h = await HarnessAsync();
        h.Forge.Files["master/coverage.yml"] = """
            mergeQueue:
              enabled: true
              autoResolve:
                - path: "**/App_Data/modelHashes.json"
                  strategy: take-base
            """;

        await h.EnqueueAsync(source: EMergeQueueCommandSource.Comment);

        var entry = await h.EntryAsync();
        entry.EvictionReason.Should().Be("configuration");
        entry.EvictionMessage.Should().Contain("postRebaseActionName");
    }

    [Fact]
    public async Task Another_label_or_a_repository_without_the_queue_is_ignored_silently()
    {
        var h = await HarnessAsync();
        await h.EnqueueAsync(label: "bug");
        (await h.EntryAsync()).Should().BeNull();

        h.Forge.Files.Remove("master/coverage.yml");
        await h.EnqueueAsync();
        (await h.EntryAsync()).Should().BeNull();
        h.Queue.QueueChecks.Should().BeEmpty();
        h.Queue.RemovedLabels.Should().BeEmpty();
    }

    [Fact]
    public async Task The_configuration_comes_from_the_default_branch_not_the_base()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Forge.PullRequests[Number] = PullRequest() with { BaseRef = "release" };
        // A widened copy on the base branch would allow "release"; the default branch's does not (O16).
        h.Forge.Files["release/coverage.yml"] = MergeQueueHarness.Yml.Replace("branches: [master]", "branches: [master, release]");

        await h.EnqueueAsync();

        var entry = await h.EntryAsync();
        entry.EvictionReason.Should().Be("branch");
    }

    // ── M6: what the repository page lists (§0 D55) ──────────────────────────────────────────────────

    [Fact]
    public async Task Every_ended_run_and_every_refusal_is_recorded_for_the_repository_page()
    {
        const string head9 = "9999999999999999999999999999999999999999";
        var h = await HarnessAsync();
        h.Forge.PullRequests[8] = PullRequest(8, "8888888888888888888888888888888888888888");
        h.Forge.PullRequests[9] = PullRequest(9, head9);
        h.Queue.Roles["triager"] = EForgeRepositoryRole.Triage;
        h.Writer.PrepareResults.Enqueue(new GitWriterPrepareResult(GitWriterOutcomes.Evicted, "conflict-outside-allow-list", BaseSha, Head, null, [],
            ["apps/A/App_Data/translations.json"], "translations.json conflicts " + new string('x', 600), 10));
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, head9, Prepared));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));

        await h.EnqueueAsync();
        await h.DrainAsync();
        await h.EnqueueAsync("triager", number: 8);
        await h.EnqueueAsync(number: 9);
        await h.DrainAsync();

        var recent = (await h.QueueDocumentAsync())!.Recent;
        recent.Select(r => (r.Number, r.State, r.Reason, r.BaseBranch)).Should().Equal([
            (7, EMergeQueueEntryState.Evicted, "conflict-outside-allow-list", "master"),
            (8, EMergeQueueEntryState.Refused, "role", "master"),
            (9, EMergeQueueEntryState.Merged, (string?)null, "master"),
        ]);
        recent[0].Message!.Length.Should().Be(MergeQueueOutcome.MaxMessageLength, "the panel lists outcomes; the full message stays on the entry");
        recent[0].Message!.Should().EndWith("…");
        recent[1].Message.Should().Contain("needs at least write access");
        recent[2].MergeCommitSha.Should().Be(MergeCommit);
        recent.Should().OnlyContain(r => r.Title == "Add widgets" && r.Run == 1 && r.FinishedAtUtc > DateTime.MinValue);

        // A replayed close of the merged entry records nothing twice.
        await h.SignalAsync(EMergeQueueSignal.Closed, 9);
        (await h.QueueDocumentAsync())!.Recent.Should().HaveCount(3);
    }

    [Fact]
    public void The_outcome_log_keeps_the_last_thirty()
    {
        var queue = new Entities.MergeQueue();
        for (var number = 1; number <= Entities.MergeQueue.MaxRecent + 5; number++)
            queue.Record(new MergeQueueOutcome { Number = number });

        queue.Recent.Select(r => r.Number).Should().Equal(Enumerable.Range(6, Entities.MergeQueue.MaxRecent));
    }

    // ── Dequeue, close, replay ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_author_may_dequeue_and_a_stranger_may_not()
    {
        var h = await HarnessAsync();
        await h.EnqueueAsync();

        await h.DequeueAsync("stranger");
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Preparing);

        await h.DequeueAsync("author");
        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Dequeued);
        entry.EvictionMessage.Should().Contain("@author");
        (await h.QueueDocumentAsync())!.IsBusy().Should().BeFalse();

        // The Start step queued before the dequeue is now stale.
        await h.DrainAsync();
        h.Writer.Prepares.Should().BeEmpty();
    }

    [Fact]
    public async Task A_closed_pull_request_leaves_the_queue()
    {
        var h = await HarnessAsync();
        await h.EnqueueAsync();

        await h.SignalAsync(EMergeQueueSignal.Closed, Number);

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Dequeued);
    }

    [Fact]
    public async Task A_replayed_command_and_a_replayed_step_change_nothing()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);

        await h.EnqueueAsync();
        await h.EnqueueAsync();
        var queued = await h.EntryAsync();
        queued.Run.Should().Be(1);
        (await h.QueueDocumentAsync())!.LaneFor("master").Waiting.Should().BeEmpty();

        await h.DrainAsync();
        var merged = await h.EntryAsync();
        merged.State.Should().Be(EMergeQueueEntryState.Merged);

        // A step delivered twice (a crash before the ack) and a webhook delivered twice.
        await h.StepAsync(h.Handled[0]);
        await h.SignalAsync(EMergeQueueSignal.ChecksChanged, sha: Head);
        await h.DrainAsync();

        var after = await h.EntryAsync();
        after.Generation.Should().Be(merged.Generation);
        after.State.Should().Be(EMergeQueueEntryState.Merged);
        h.Queue.MergeRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_refused_permission_evicts_with_the_acceptance_link()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Permissions.Add(new FixedPermissions("https://github.com/organizations/acme/settings/installations/4242"));
        h.Queue.MergeThrows = new ForgeAccessDeniedException("GitHub answered 403 to PUT …/merge-async: Resource not accessible by integration", "contents=write");

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.EvictionReason.Should().Be("permission");
        entry.PermissionsUrl.Should().Be("https://github.com/organizations/acme/settings/installations/4242", "O17");
        h.Queue.LastCheck.Summary.Should().Contain("accept the new permissions");
    }

    [Fact]
    public async Task A_transient_forge_failure_retries_the_same_step_later()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Queue.MergeThrows = new ForgeRequestException("GitHub answered 502", 502);

        await h.EnqueueAsync();
        await h.DrainAsync();

        var waiting = await h.EntryAsync();
        waiting.State.Should().Be(EMergeQueueEntryState.Merging);
        waiting.TransientFailures.Should().Be(1);

        await h.ReleaseAsync(EMergeQueueStep.Merge);
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
    }

    /// <summary>
    /// §0 D58: <c>MaxTransientRetries</c> retries follow the first failure, so the seventh failure in a row evicts, and the
    /// message says seven. It said "after 6 failed attempts" on the seventh.
    /// </summary>
    [Fact]
    public async Task Seven_transient_failures_in_a_row_evict_and_the_message_counts_them()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Queue.MergeThrows = new ForgeRequestException("GitHub answered 502", 502);
        await h.EnqueueAsync();
        await h.DrainAsync();

        for (var retry = 1; retry <= MergeQueueEngine.MaxTransientRetries; retry++)
        {
            (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merging, $"failure {retry} is retried");
            h.Queue.MergeThrows = new ForgeRequestException("GitHub answered 502", 502);
            await h.ReleaseAsync(EMergeQueueStep.Merge);
        }

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("error");
        entry.EvictionMessage.Should().Contain("7 failed attempts");
        h.Queue.MergeRequests.Should().BeEmpty();
    }

    /// <summary>
    /// §0 D57: a request the git writer refuses is refused again on every retry, so it ends the entry at once, with the
    /// writer's reason. Before, the 400 was a transient <see cref="HttpRequestException"/>: six retries, then <c>error</c>.
    /// </summary>
    [Fact]
    public async Task A_request_the_writer_refuses_evicts_at_once_with_its_reason()
    {
        var h = await HarnessAsync();
        var writer = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(System.Net.HttpStatusCode.BadRequest,
            """{"outcome":"refused","field":"regeneratedPaths","detail":"0 to 64 globs, each 1 to 256 characters"}"""));
        h.WriterOverride = new GitWriterSidecarClient(new HttpClient(writer) { BaseAddress = new Uri("http://writer/") },
            Microsoft.Extensions.Options.Options.Create(new MergeQueueOptions()));

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted, "the same request would be refused every time");
        entry.EvictionReason.Should().Be("writer-refused");
        entry.EvictionMessage.Should().Contain("regeneratedPaths");
        writer.Requests.Should().ContainSingle();
    }

    /// <summary>
    /// §0 D57: GitHub allows branch names the git writer does not push to (a '+', for one). Publish would be refused on every
    /// attempt, so a pull request that needs a rebase from such a branch is told so before the writer is asked anything.
    /// </summary>
    [Fact]
    public async Task A_branch_name_the_writer_does_not_push_to_evicts_before_the_writer_is_asked()
    {
        var h = await HarnessAsync();
        h.Forge.PullRequests[Number] = PullRequest() with { HeadRef = "feature+widgets" };
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("branch-name");
        h.Writer.Prepares.Should().BeEmpty();
        h.Writer.Publishes.Should().BeEmpty();
    }

    // ── Changes under .github/** (D12, O21 A, §0 D63) ──────────────────────────────────────────────────

    private static GitWriterPrepareResult TouchesWorkflows(GitWriterPrepareResult prepared, params string[] paths)
        => prepared with { TouchesWorkflows = true, Paths = paths };

    /// <summary>O21 A: without a dispatch, the pull request's own CI already ran these workflows exactly as they would
    /// run without the queue, so a workflow-only change (a Dependabot Actions bump) goes through.</summary>
    [Fact]
    public async Task A_same_repository_change_under_github_that_needs_no_regeneration_is_published_and_merged()
    {
        var h = await HarnessAsync();
        h.Forge.Comparison = new CommitComparison(OldBase, [new DiffFile(".github/workflows/ci.yml", "modified", null, [])], false);
        h.Writer.PrepareResults.Enqueue(TouchesWorkflows(FakeGitWriter.Prepared(BaseSha, Head, Prepared), ".github/workflows/ci.yml"));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        h.Writer.Prepares.Single().AllowWorkflowChanges.Should().BeTrue("a same-repository branch's push runs nothing its author could not run");
        h.Queue.Dispatches.Should().BeEmpty();
        // §0 D59: the change touches .github/workflows/**, so both pushes need GitHub's workflow permission.
        h.Queue.WriterAccesses.Select(a => a.Workflows).ToList().Should().Equal([true, true, false]);
    }

    /// <summary>O21 A, T5: a dispatched run executes the workflow files of the queue branch (D12), so a change under
    /// .github/** that needs the regenerate workflow ends there, before the dispatch.</summary>
    [Fact]
    public async Task A_same_repository_change_under_github_that_needs_regeneration_is_evicted_before_the_dispatch()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(TouchesWorkflows(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"), ".github/actions/build/action.yml"));

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("touches-workflows");
        entry.EvictionPaths.Should().Equal([".github/actions/build/action.yml"]);
        h.Queue.Dispatches.Should().BeEmpty();
        h.Writer.Publishes.Should().BeEmpty();
    }

    /// <summary>§0 D63: a fork's change under .github/** is refused by the writer before any push, because the push to the
    /// queue branch of the base repository would already start its push workflows from the fork's files.</summary>
    [Fact]
    public async Task A_forks_change_under_github_is_refused_before_anything_is_pushed()
    {
        var h = await HarnessAsync();
        h.Forge.PullRequests[Number] = PullRequest(fork: true, maintainerCanModify: true);
        h.Writer.PrepareResults.Enqueue(new GitWriterPrepareResult(GitWriterOutcomes.Evicted, "touches-workflows", BaseSha, Head, null, [],
            [".github/workflows/ci.yml"], "The pull request changes .github/**.", 10));

        await h.EnqueueAsync();
        await h.DrainAsync();

        h.Writer.Prepares.Single().AllowWorkflowChanges.Should().BeFalse();
        var entry = await h.EntryAsync();
        entry.EvictionReason.Should().Be("touches-workflows");
        entry.EvictionMessage.Should().Contain("fork");
    }

    // ── O22: the bypass role (§0 D64) ───────────────────────────────────────────────────────────────────

    private static async Task SetBypassRoleAsync(MergeQueueHarness h, EMergeQueueBypassRole role)
    {
        using var session = h.Store.OpenAsyncSession();
        var account = await session.LoadAsync<Account>(AccountId);
        account.MergeQueueBypassRole = role;
        await session.SaveChangesAsync();
    }

    /// <summary>A same-repository .github change that needs the regenerate workflow, queued by <paramref name="actor"/>; the
    /// run succeeds with nothing to change, and the final commit's checks are <paramref name="finalChecks"/>.</summary>
    private static void RegeneratedGitHubChange(MergeQueueHarness h, ForgeCheckState? finalChecks = null)
    {
        h.Writer.PrepareResults.Enqueue(TouchesWorkflows(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"), ".github/workflows/ci.yml"));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "completed", "success", Prepared, "mq/master/pr-7", null);
        h.Queue.Artifacts[9001] = new ForgeArtifactDownload(EForgeArtifactOutcome.Ok, "patch.diff", [], null);
        if (finalChecks is not null) h.Queue.CheckStates[Final] = finalChecks;
    }

    [Fact]
    public async Task The_bypass_role_skips_the_approval_floor_for_its_holders_only()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        await SetBypassRoleAsync(h, EMergeQueueBypassRole.Maintain);
        h.Forge.PullRequests[Number] = PullRequest(author: "contributor");
        h.Queue.Roles["lead"] = EForgeRepositoryRole.Maintain;

        await h.EnqueueAsync("maintainer");
        (await h.EntryAsync()).EvictionReason.Should().Be("approval", "write is below the bypass role");

        await h.EnqueueAsync("lead");
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.Config.Values.Should().Contain(v => v.Key == "bypassRole" && v.Value == "Maintain" && v.Source == "organization", "O15: the check run says where it came from");
    }

    [Fact]
    public async Task The_bypass_role_lets_a_same_repository_github_change_through_the_dispatch()
    {
        var h = await HarnessAsync();
        await SetBypassRoleAsync(h, EMergeQueueBypassRole.Admin);
        h.Queue.Roles["owner"] = EForgeRepositoryRole.Admin;
        RegeneratedGitHubChange(h);

        await h.EnqueueAsync("owner");
        await h.DrainAsync();
        h.Queue.Dispatches.Should().ContainSingle("O22: the bypass role lets the regenerate workflow run");
        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9001);
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
    }

    [Fact]
    public async Task The_bypass_role_never_lets_the_regenerate_workflow_differ_from_the_default_branchs()
    {
        var h = await HarnessAsync();
        await SetBypassRoleAsync(h, EMergeQueueBypassRole.Admin);
        h.Queue.Roles["owner"] = EForgeRepositoryRole.Admin;
        RegeneratedGitHubChange(h);
        h.Queue.Blobs[$"{Prepared}:.github/workflows/regenerate.yml"] = "e7e7";

        await h.EnqueueAsync("owner");
        await h.DrainAsync();

        (await h.EntryAsync()).EvictionReason.Should().Be("workflow-differs", "D12's identity is never bypassed");
        h.Queue.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task The_bypass_role_never_lets_a_forks_github_change_through()
    {
        var h = await HarnessAsync();
        await SetBypassRoleAsync(h, EMergeQueueBypassRole.Maintain);
        h.Queue.Roles["owner"] = EForgeRepositoryRole.Admin;
        h.Forge.PullRequests[Number] = PullRequest(fork: true, maintainerCanModify: true);
        h.Writer.PrepareResults.Enqueue(new GitWriterPrepareResult(GitWriterOutcomes.Evicted, "touches-workflows", BaseSha, Head, null, [],
            [".github/workflows/ci.yml"], "The pull request changes .github/**.", 10));

        await h.EnqueueAsync("owner");
        await h.DrainAsync();

        h.Writer.Prepares.Single().AllowWorkflowChanges.Should().BeFalse("T5: a fork's change is refused before any push, whoever queued it");
        (await h.EntryAsync()).EvictionReason.Should().Be("touches-workflows");
    }

    [Fact]
    public async Task A_bypass_role_lost_before_the_merge_evicts_the_change_it_let_through()
    {
        var h = await HarnessAsync();
        await SetBypassRoleAsync(h, EMergeQueueBypassRole.Admin);
        h.Queue.Roles["owner"] = EForgeRepositoryRole.Admin;
        RegeneratedGitHubChange(h, Pending(Final));
        await h.EnqueueAsync("owner");
        await h.DrainAsync();
        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9001);
        await h.DrainAsync();
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.AwaitingChecks);

        h.Queue.Roles["owner"] = EForgeRepositoryRole.Write;
        h.Queue.CheckStates[Final] = Green(Final);
        await h.SignalAsync(EMergeQueueSignal.ChecksChanged, sha: Final);
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.EvictionReason.Should().Be("bypass-revoked", "O22 is re-checked before the merge, like O9");
        h.Queue.MergeRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_bypass_role_lost_before_the_merge_brings_the_approval_floor_back()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        await SetBypassRoleAsync(h, EMergeQueueBypassRole.Maintain);
        h.Forge.PullRequests[Number] = PullRequest(author: "contributor");
        h.Queue.Roles["lead"] = EForgeRepositoryRole.Maintain;
        h.Queue.CheckStates[Head] = Pending(Head);
        await h.EnqueueAsync("lead");
        await h.DrainAsync();
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.AwaitingChecks);

        h.Queue.Roles["lead"] = EForgeRepositoryRole.Write;
        h.Queue.CheckStates[Head] = Green(Head);
        await h.SignalAsync(EMergeQueueSignal.ChecksChanged, sha: Head);
        await h.DrainAsync();

        (await h.EntryAsync()).EvictionReason.Should().Be("approval-withdrawn");
        h.Queue.MergeRequests.Should().BeEmpty();
    }

    // ── Co-authors (§0 D60) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The pull request's commits as GitHub lists them: the author's, a pair's crediting a third person, a bot's,
    /// the pair's again under another case, and the author's again from an address GitHub links to no account.</summary>
    private static ForgePullRequestCommit[] MixedCommits() =>
    [
        new("c1", "The Author", "author@example.invalid", "author", false, "Add widgets"),
        new("c2", "Pair Partner", "pair@example.invalid", "pair", false, "Fix widgets\n\nCo-authored-by: Third Person <third@example.invalid>\n"),
        new("c3", "dependabot[bot]", "49699333+dependabot[bot]@users.noreply.github.com", "dependabot[bot]", true, "Bump widgets"),
        new("c4", "Pair Partner", "PAIR@example.invalid", "pair", false, "Again"),
        new("c5", "The Author", "12345+author@users.noreply.github.com", null, false, "From the web editor"),
    ];

    private const string CoAuthored = "Co-authored-by: Pair Partner <pair@example.invalid>\nCo-authored-by: Third Person <third@example.invalid>";

    [Fact]
    public async Task The_squashed_commit_and_the_merge_credit_the_other_authors_but_not_the_author_or_a_bot()
    {
        var h = await HarnessAsync();
        h.Queue.Commits.AddRange(MixedCommits());
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        h.Writer.Prepares.Single().Message.Should().Be($"Add widgets (#7)\n\n{CoAuthored}");
        h.Queue.MergeMessages.Should().Equal([("Add widgets (#7)", CoAuthored)], "GitHub builds the merged commit from the merge's own message");
    }

    [Fact]
    public async Task A_direct_merge_credits_the_other_authors_too_and_a_pull_request_of_one_author_credits_nobody()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Queue.Commits.AddRange(MixedCommits());

        await h.EnqueueAsync();
        await h.DrainAsync();

        h.Queue.MergeMessages.Should().Equal([("Add widgets (#7)", CoAuthored)]);

        var alone = await HarnessAsync(mergeBase: BaseSha);
        alone.Queue.Commits.Add(new("c1", "The Author", "author@example.invalid", "author", false, "Add widgets"));
        await alone.EnqueueAsync();
        await alone.DrainAsync();

        alone.Queue.MergeMessages.Should().Equal([("Add widgets (#7)", (string?)null)]);
    }

    [Fact]
    public void Co_authors_the_writer_would_refuse_are_skipped_and_their_number_is_bounded()
    {
        ForgePullRequestCommit[] commits =
        [
            new("c1", "Spaced Email", "has space@example.invalid", "spaced", false, "x"),
            new("c2", "  ", "blank@example.invalid", "blank", false, "x"),
            new("c3", "The Queue", "merge-queue@noreply.coverage.mintplayer.com", null, false, "x"),
            new("c4", "Many", "many@example.invalid", "many", false,
                string.Join('\n', Enumerable.Range(1, MergeQueueEngine.MaxCoAuthors + 20).Select(i => $"Co-authored-by: Person {i} <p{i}@example.invalid>"))),
        ];

        var trailers = MergeQueueEngine.CoAuthorTrailers(commits, "author", "merge-queue@noreply.coverage.mintplayer.com");

        trailers.Should().HaveCount(MergeQueueEngine.MaxCoAuthors);
        trailers[0].Should().Be("Co-authored-by: Many <many@example.invalid>");
        trailers.Should().NotContain(t => t.Contains("space@") || t.Contains("blank@") || t.Contains("merge-queue@"));
    }

    // ── T9: a credential per writer job (§0 D59) ───────────────────────────────────────────────────────

    [Fact]
    public async Task Every_writer_job_gets_a_credential_of_its_own_and_ends_it()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        h.Queue.WriterAccesses.Should().Equal([((int?)null, false, "job-token-1"), (Number, false, "job-token-2"), (null, false, "job-token-3")]);
        h.Writer.Prepares.Single().Credential.Password.Should().Be("job-token-1");
        h.Writer.Publishes.Single().Credential.Password.Should().Be("job-token-2");
        h.Writer.Deletes.Single().Credential.Password.Should().Be("job-token-3", "the queue branch's deletion gets its own too");
        h.Queue.RevokedTokens.Should().Equal(["job-token-1", "job-token-2", "job-token-3"]);
    }

    [Fact]
    public async Task A_credential_ends_with_its_job_even_when_the_job_fails()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishThrows = new HttpRequestException("The response ended prematurely.");

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).TransientFailures.Should().Be(1);
        h.Queue.RevokedTokens.Should().Equal([.. h.Queue.WriterAccesses.Select(a => a.Token)]);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task Only_a_forks_publish_after_the_base_changed_its_workflows_asks_for_the_workflow_permission(bool fork, bool baseChangedWorkflows, bool workflows)
    {
        var h = await HarnessAsync();
        h.Forge.PullRequests[Number] = PullRequest(fork: fork, maintainerCanModify: true);
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared) with { BaseChangedWorkflows = baseChangedWorkflows });
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));

        await h.EnqueueAsync();
        await h.DrainAsync();

        h.Queue.WriterAccesses.Select(a => (a.Number, a.Workflows)).ToList().Should().Equal([((int?)null, false), (Number, workflows), (null, false)]);
        h.Writer.Publishes.Single().PushUrl.Should().Be(fork ? "https://github.com/contributor/widget.git" : null);
    }

    // ── Re-checked before the merge (O9, O11, §0 D61) ───────────────────────────────────────────────

    [Fact]
    public async Task An_approval_withdrawn_before_the_merge_evicts_without_merging()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Forge.PullRequests[Number] = PullRequest(author: "contributor");
        h.Queue.Reviews.Add(new ForgeReview(1, "maintainer", EForgeReviewState.Approved, Head, null));
        h.Queue.CheckStates[Head] = Pending(Head);
        await h.EnqueueAsync();
        await h.DrainAsync();
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.AwaitingChecks);

        // GitHub dismisses a review in place: the same review, now DISMISSED.
        h.Queue.Reviews[0] = h.Queue.Reviews[0] with { State = EForgeReviewState.Dismissed };
        h.Queue.CheckStates[Head] = Green(Head);
        await h.SignalAsync(EMergeQueueSignal.ChecksChanged, sha: Head);
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("approval-withdrawn", "O11 is checked again right before the merge, like O9's role");
        h.Queue.MergeRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_writers_change_request_since_enqueue_evicts_before_the_merge()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Queue.Roles["reviewer"] = EForgeRepositoryRole.Write;
        h.Queue.CheckStates[Head] = Pending(Head);
        // The author has write, so the default floor needs no approval at enqueue.
        await h.EnqueueAsync();
        await h.DrainAsync();
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.AwaitingChecks);

        h.Queue.Reviews.Add(new ForgeReview(1, "reviewer", EForgeReviewState.ChangesRequested, Head, null));
        h.Queue.CheckStates[Head] = Green(Head);
        await h.SignalAsync(EMergeQueueSignal.ChecksChanged, sha: Head);
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("approval-withdrawn");
        entry.EvictionMessage.Should().Contain("@reviewer");
        h.Queue.MergeRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_standing_change_request_by_a_writer_refuses_the_enqueue_but_one_by_a_reader_does_not()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Queue.Roles["reviewer"] = EForgeRepositoryRole.Write;
        h.Queue.Roles["passer-by"] = EForgeRepositoryRole.Read;
        h.Queue.Reviews.Add(new ForgeReview(1, "reviewer", EForgeReviewState.ChangesRequested, Head, null));

        await h.EnqueueAsync();
        var refused = await h.EntryAsync();
        refused.State.Should().Be(EMergeQueueEntryState.Refused);
        refused.EvictionReason.Should().Be("approval");
        refused.EvictionMessage.Should().Contain("@reviewer");

        // The reviewer approves; a reader's change request is no veto.
        h.Queue.Reviews.Add(new ForgeReview(2, "reviewer", EForgeReviewState.Approved, Head, null));
        h.Queue.Reviews.Add(new ForgeReview(3, "passer-by", EForgeReviewState.ChangesRequested, Head, null));
        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
    }

    /// <summary>§0 D62: the queue's own check is in progress while it merges, so a branch that requires it can never merge.</summary>
    [Fact]
    public async Task A_branch_that_requires_the_queues_own_check_is_refused()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Queue.Rules = h.Queue.Rules with { RequiredChecks = ["build", CheckGate.QueueCheckName] };

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Refused);
        entry.EvictionReason.Should().Be("self-required");
        entry.EvictionMessage.Should().Contain($"\"{CheckGate.QueueCheckName}\"");
        h.Queue.MergeRequests.Should().BeEmpty();
    }

    private sealed class FixedPermissions(string url) : IForgeInstallationPermissions
    {
        public EForgeProvider Provider => EForgeProvider.GitHub;

        public Task<PendingInstallationPermissions?> GetPendingAsync(Account account, CancellationToken cancellationToken = default)
            => Task.FromResult<PendingInstallationPermissions?>(new PendingInstallationPermissions(url, [new PendingPermission("actions", "Actions", "write", null)]));
    }
}
