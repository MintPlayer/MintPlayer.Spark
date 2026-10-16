using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.MergeQueue;
using CodeCoverage.Tests.Services;
using Xunit;
using GitHubInstallationPermissions = CodeCoverage.Services.GitHubInstallationPermissions;
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
        prepare.Message.Should().Be("Add widgets (#7)");
        prepare.AutoResolve.Should().Equal([new GitWriterAutoResolve("**/App_Data/modelHashes.json", AnyLine: true)]);

        var publish = h.Writer.Publishes.Should().ContainSingle().Which;
        publish.Patch.Should().BeNull("nothing was resolved, so nothing was regenerated");
        publish.PreparedSha.Should().Be(Prepared);
        publish.Prepare.HeadSha.Should().Be(Head, "the lease is the pinned head");
        publish.Prepare.BaseSha.Should().Be(BaseSha, "§0 D65: the writer rebuilds the prepared commit on the base it was built on");
        publish.PrBranch.Should().Be("feature-7");
        publish.PushUrl.Should().BeNull();
        h.Queue.Dispatches.Should().BeEmpty();
        h.Queue.MergeRequests.Should().Equal([(Number, Final)]);

        // §0 D65: nothing was dispatched, so the queue branch was never pushed, and there is nothing to delete. Before, the
        // prepare pushed it on every run and this test asserted its deletion under a lease on Prepared.
        h.Writer.QueueBranchPushes.Should().BeEmpty();
        h.Writer.Deletes.Should().BeEmpty();
        entry.QueueBranchSha.Should().BeNull();
    }

    /// <summary>
    /// §0 D65: the writer rebuilds the prepared commit for the queue-branch push and the publish, so every job of a run gets
    /// the same fields, and every date is the run's (when it was queued, to the second). Before, the saga sent no date: git
    /// stamped the time, and no job could build the same commit twice.
    /// </summary>
    [Fact]
    public async Task Every_job_of_a_run_builds_the_same_commit_from_the_same_fields_and_the_runs_date()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final, "apps/A/App_Data/modelHashes.json"));
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "completed", "success", Prepared, "mq/master/pr-7", null);
        h.Queue.Artifacts[9001] = new ForgeArtifactDownload(EForgeArtifactOutcome.Ok, "patch.diff", "diff --git a/x b/x\n"u8.ToArray(), null);

        await h.EnqueueAsync();
        await h.DrainAsync();
        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9001);
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        var date = MergeQueueEngine.CommitDate(entry);
        date.Offset.Should().Be(TimeSpan.Zero);
        (date.Ticks % TimeSpan.TicksPerSecond).Should().Be(0, "git dates are whole seconds");
        var prepare = h.Writer.Prepares.Should().ContainSingle().Which;
        var push = h.Writer.QueueBranchPushes.Should().ContainSingle().Which;
        var publish = h.Writer.Publishes.Should().ContainSingle().Which;
        foreach (var rebuild in new[] { push.Prepare, publish.Prepare })
        {
            rebuild.Author.Should().Be(prepare.Author);
            rebuild.Committer.Should().Be(prepare.Committer);
            rebuild.Message.Should().Be(prepare.Message);
            rebuild.HeadSha.Should().Be(prepare.HeadSha);
            rebuild.HeadRef.Should().Be(prepare.HeadRef);
            rebuild.BaseBranch.Should().Be(prepare.BaseBranch);
            rebuild.BaseSha.Should().Be(BaseSha);
            rebuild.AutoResolve.Should().Equal(prepare.AutoResolve);
        }
        prepare.Author.Date.Should().Be(date);
        prepare.Committer.Date.Should().Be(date);
        push.PreparedSha.Should().Be(Prepared);
        push.HeadFromFork.Should().BeFalse("O25, §0 D67: the writer is told where the head lives");
        publish.PreparedSha.Should().Be(Prepared);
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
        // §0 D65: the dispatch is the one step that writes the queue branch, leased on "absent" for a new run.
        var push = h.Writer.QueueBranchPushes.Should().ContainSingle().Which;
        push.QueueBranch.Should().Be("mq/master/pr-7");
        push.PreparedSha.Should().Be(Prepared);
        push.ExpectedQueueSha.Should().BeNull();
        (await h.EntryAsync()).QueueBranchSha.Should().Be(Prepared);
        h.Queue.WorkflowFileReads.Should().BeEmpty("a same-repository pull request needs no fork precondition (§0 D65)");

        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9001, sha: Prepared);
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.Dispatches.Should().ContainSingle().Which.RunId.Should().Be(9001);
        h.Writer.Publishes.Should().ContainSingle().Which.Patch.Should().Be(Convert.ToBase64String("diff --git a/x b/x\n"u8.ToArray()));
        h.Queue.CancelledRuns.Should().BeEmpty("the run ended the wait itself (§0 D53)");
        // D5: the branch the dispatch needed goes under a lease on the commit the run pushed there.
        h.Writer.Deletes.Should().ContainSingle().Which.ExpectedSha.Should().Be(Prepared);
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
        // Red, so the author sees an X to act on (owner, 2026-10-15), with the button to queue it again (§0 O30).
        var ended = h.Queue.QueueChecks.Last(c => c.Sha == Head).Check;
        ended.State.Should().Be(EForgeQueueCheckState.Failure);
        ended.Title.Should().Be("Evicted: conflict-outside-allow-list");
        ended.Actions.Should().Equal([ForgeQueueCheckAction.Enqueue]);

        (await h.EntryAsync(8)).State.Should().Be(EMergeQueueEntryState.Preparing, "D11: the lane moves on to the next entry");
        (await h.QueueDocumentAsync())!.LaneFor("master").Current.Should().Be(MergeQueueEntry.DocumentId(RepositoryId, 8));
    }

    /// <summary>
    /// §0 D72: the comment says once why a person must resolve the conflict. The writer's detail for a file outside the
    /// allow-list is that reason, so the saga's own sentence only says what to do. Before, the comment read "Only generated
    /// files are resolved automatically (O1); rebase by hand and queue it again." and then the writer's "Only allow-listed
    /// generated files are resolved; these conflicts need a human.", before its own "Queue it again" line.
    /// </summary>
    [Fact]
    public async Task A_conflict_outside_the_allow_list_is_explained_once()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(new GitWriterPrepareResult(GitWriterOutcomes.Evicted, "conflict-outside-allow-list", BaseSha, Head, null, [],
            ["apps/A/App_Data/translations.json"], CodeCoverage.Git.MergeQueue.MergeQueueResolver.OutsideAllowListDetail, 10));

        await h.EnqueueAsync();
        await h.DrainAsync();

        h.Queue.Comments.Should().ContainSingle().Which.Body.Should().Be(
            "The merge queue removed this pull request: Rebasing onto `master` conflicts in files the queue may not resolve "
            + "(conflict-outside-allow-list). Rebase it onto `master` by hand.\n\n"
            + "Only allow-listed generated files are resolved automatically.\n\n"
            + "Queue it again with `/queue` or the `merge-queue` label.");
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
        h.Writer.Publishes[1].Prepare.HeadSha.Should().Be(Head, "the lease is still the pinned head: the writer, not the saga, recognises the commit");

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
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"));
        await h.EnqueueAsync();
        await h.StepOnceAsync();
        await h.StepOnceAsync();
        (await h.EntryAsync()).QueueBranchSha.Should().Be(Prepared, "run 1 dispatched, so it pushed the queue branch");
        await h.DequeueAsync("author");
        // Run 1's cleanup of mq/master/pr-7 (leased on Prepared) is held back, as a busy writer would delay it.
        var cleanup = h.Outbox.Messages.Select(m => m.Message).OfType<MergeQueueStepMessage>().Single(s => s.Step == EMergeQueueStep.Cleanup);
        cleanup.ExpectedSha.Should().Be(Prepared);
        h.Outbox.Messages.Clear();

        // Run 2's push finds the very commit on the queue branch and adopts it (since §0 D65 that needs the same date,
        // so two runs queued within one second; the lease keeps any other commit safe anyway).
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"));
        h.Writer.QueueBranchResults.Enqueue(FakeGitWriter.Pushed(Prepared, GitWriterReasons.AlreadyPushed));
        h.Queue.NextDispatch = new ForgeWorkflowDispatch(EForgeDispatchOutcome.Dispatched, 9002, null, null);
        await h.EnqueueAsync();
        await h.StepOnceAsync();
        await h.StepOnceAsync();
        var adopted = await h.EntryAsync();
        adopted.Run.Should().Be(2);
        adopted.QueueBranchSha.Should().Be(Prepared);
        h.Writer.QueueBranchPushes[1].ExpectedQueueSha.Should().BeNull("a new run leases on an absent branch");

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
            entry.Dispatches.Should().ContainSingle().Which.RunId.Should().Be(9002);
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
        lost.Dispatches.Should().BeEmpty("run 9001 started, but the entry never learnt its id");

        h.Queue.NextDispatch = new(EForgeDispatchOutcome.Dispatched, 9002, null, null);
        await h.ReleaseAsync(EMergeQueueStep.Dispatch);

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Regenerating);
        entry.Dispatches.Should().ContainSingle().Which.RunId.Should().Be(9002);
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
        h.Writer.Publishes[1].Prepare.HeadSha.Should().Be(Final, "the lease is the head the second prepare was built from");
        h.Writer.Publishes[1].Prepare.BaseSha.Should().Be(NewBase);
        h.Writer.QueueBranchPushes.Should().BeEmpty("neither attempt dispatched, so neither wrote a queue branch (§0 D65)");

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
        // O18's advice, in words: the message no longer cites the decision (§0 D72).
        entry.EvictionMessage.Should().Contain("someone other than the last pusher").And.Contain("turn those off for branches the queue manages");
    }

    /// <summary>
    /// §0 D94, live 2026-10-15: GitHub's <c>pull_request.closed</c> (merged) webhook beat the 15 s merge poll, and the
    /// Merged signal completed the entry with a hard-coded null merge commit. The signal now carries the event's
    /// <c>merge_commit_sha</c>, and the poll that comes after changes nothing.
    /// </summary>
    [Fact]
    public async Task A_merged_event_that_beats_the_poll_records_the_merge_commit()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        var uuid = "d45ff22b-73cf-44a4-9eb0-cbf81c955578";
        h.Queue.MergeReplies.Enqueue(new ForgeMergeRequest(EForgeMergeState.Pending, uuid, null, "enqueued"));

        await h.EnqueueAsync();
        await h.DrainAsync();
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merging);

        await h.SignalAsync(new MergeQueueSignalMessage { RepositoryId = RepositoryId, Kind = EMergeQueueSignal.Merged, Number = Number, Sha = MergeCommit });
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.MergeCommitSha.Should().Be(MergeCommit);
        (await h.QueueDocumentAsync())!.Recent.Last().MergeCommitSha.Should().Be(MergeCommit);

        await h.ReleaseAsync(EMergeQueueStep.PollMerge);

        var after = await h.EntryAsync();
        after.State.Should().Be(EMergeQueueEntryState.Merged);
        after.MergeCommitSha.Should().Be(MergeCommit);
        after.Generation.Should().Be(entry.Generation);
        h.Queue.MergeRequests.Should().HaveCount(1);
    }

    /// <summary>§0 D94: an event without the sha still records it, from the merge request's own status.</summary>
    [Fact]
    public async Task A_merged_event_without_the_sha_reads_it_from_the_merge_request()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        var uuid = "d45ff22b-73cf-44a4-9eb0-cbf81c955578";
        h.Queue.MergeReplies.Enqueue(new ForgeMergeRequest(EForgeMergeState.Pending, uuid, null, "enqueued"));

        await h.EnqueueAsync();
        await h.DrainAsync();
        h.Queue.MergeStatuses.Enqueue(new ForgeMergeRequest(EForgeMergeState.Merged, uuid, MergeCommit, null));

        await h.SignalAsync(EMergeQueueSignal.Merged, Number);
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.MergeCommitSha.Should().Be(MergeCommit);
    }

    // ── The linked issues after the merge (§0 D106) ─────────────────────────────────────────────────

    private const string ClosedByComment = "Closed by #7 (merged by the merge queue).";

    /// <summary>One open issue of this repository, one of another repository, and one already closed.</summary>
    private static List<ForgeClosingIssue> LinkedIssues() =>
    [
        new(284, "acme/widget", true),
        new(12, "acme/other", true),
        new(154, "acme/widget", false),
    ];

    /// <summary>
    /// §0 D106, live 2026-10-15: GitHub left #284 open after the queue merged #518, which lists it among its closing
    /// issues. After the merge, the queue closes the open ones of its own repository, and only those.
    /// </summary>
    [Fact]
    public async Task After_a_merge_the_open_linked_issues_of_the_repository_are_closed()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Queue.ClosingIssues[Number] = LinkedIssues();

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        h.Queue.ClosingIssueQueries.Should().BeEmpty("the step waits, so GitHub's own close comes first when there is one");
        h.Delayed.Should().ContainSingle(s => s.Step == EMergeQueueStep.CloseIssues);

        await h.ReleaseAsync(EMergeQueueStep.CloseIssues);

        h.Queue.ClosedIssues.Should().Equal([(284, ClosedByComment)], "another repository's issue and a closed one are left alone");
        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.History.Should().Contain(e => e.Text == "closed the linked issues #284");
    }

    [Fact]
    public async Task A_redelivered_close_step_closes_nothing_twice()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Queue.ClosingIssues[Number] = LinkedIssues();
        await h.EnqueueAsync();
        await h.ReleaseAsync(EMergeQueueStep.CloseIssues);

        await h.StepAsync(h.Handled[^1]);

        h.Queue.ClosingIssueQueries.Should().Equal([Number, Number]);
        h.Queue.ClosedIssues.Should().ContainSingle("the issue the first delivery closed is closed now");
    }

    /// <summary>A merge completed by the merged event, then by the late poll, queues the close once.</summary>
    [Fact]
    public async Task A_merge_completed_by_the_event_queues_the_close_once()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Queue.MergeReplies.Enqueue(new ForgeMergeRequest(EForgeMergeState.Pending, "d45ff22b-73cf-44a4-9eb0-cbf81c955578", null, "enqueued"));
        await h.EnqueueAsync();
        await h.DrainAsync();

        await h.SignalAsync(new MergeQueueSignalMessage { RepositoryId = RepositoryId, Kind = EMergeQueueSignal.Merged, Number = Number, Sha = MergeCommit });
        await h.ReleaseAsync(EMergeQueueStep.PollMerge);

        h.Delayed.Where(s => s.Step == EMergeQueueStep.CloseIssues).Should().ContainSingle();
    }

    [Fact]
    public async Task A_refused_close_leaves_the_merge_standing_and_says_so_on_the_pull_request()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Queue.ClosingIssues[Number] = LinkedIssues();
        h.Queue.CloseIssueThrows = new ForgeAccessDeniedException("GitHub answered 403 to PATCH repos/acme/widget/issues/284: Resource not accessible by integration", "issues=write");
        await h.EnqueueAsync();

        await h.ReleaseAsync(EMergeQueueStep.CloseIssues);

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged, "closing the issues is best effort after the merge");
        entry.MergeCommitSha.Should().Be(MergeCommit);
        entry.History.Should().Contain(e => e.Text.StartsWith("could not close the linked issues", StringComparison.Ordinal));
        h.Queue.ClosedIssues.Should().BeEmpty();
        h.Queue.Comments.Should().Contain(c => c.Number == Number && c.Body.Contains("refused to let it close the issues it links") && c.Body.Contains("`issues=write`"));
    }

    [Fact]
    public async Task A_close_that_may_pass_is_retried_by_the_runtime_and_then_closes()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Queue.ClosingIssues[Number] = LinkedIssues();
        h.Queue.CloseIssueThrows = new ForgeRequestException("GitHub answered 502 to PATCH repos/acme/widget/issues/284", 502);
        await h.EnqueueAsync();

        var release = () => h.ReleaseAsync(EMergeQueueStep.CloseIssues);
        await release.Should().ThrowAsync<ForgeRequestException>("the messaging runtime retries a step that fails like this");
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged, "the merge stands");

        await h.StepAsync(h.Handled[^1]);

        h.Queue.ClosedIssues.Should().Equal([(284, ClosedByComment)]);
    }

    [Fact]
    public async Task A_merge_that_fails_closes_nothing()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Queue.ClosingIssues[Number] = LinkedIssues();
        h.Queue.MergeReplies.Enqueue(new ForgeMergeRequest(EForgeMergeState.Failed, null, null, "Repository rule violations found"));

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Evicted);
        h.Delayed.Should().NotContain(s => s.Step == EMergeQueueStep.CloseIssues);
        h.Queue.ClosingIssueQueries.Should().BeEmpty();
        h.Queue.ClosedIssues.Should().BeEmpty();
    }

    /// <summary>GitHub closes linked issues only on a merge into the default branch, and the queue does no more.</summary>
    [Fact]
    public async Task A_merge_into_a_branch_that_is_not_the_default_closes_nothing()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Queue.ClosingIssues[Number] = LinkedIssues();
        await h.EnqueueAsync();
        await h.DrainAsync();
        using (var session = h.Store.OpenAsyncSession())
        {
            (await session.LoadAsync<Entities.Repository>(RepositoryId))!.DefaultBranch = "main";
            await session.SaveChangesAsync();
        }

        await h.ReleaseAsync(EMergeQueueStep.CloseIssues);

        h.Queue.ClosingIssueQueries.Should().BeEmpty();
        h.Queue.ClosedIssues.Should().BeEmpty();
    }

    /// <summary>§0 D106: an App without <c>issues: write</c> still merges; the check warns that the issues may stay open.</summary>
    [Fact]
    public async Task An_app_without_issues_write_warns_at_enqueue_and_still_merges()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        var app = FixedPermissions.FullApp();
        app["issues"] = "read";
        h.Permissions.Add(new FixedPermissions("https://github.com/organizations/acme/settings/installations/4242") { AppPermissions = app });

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.Warnings.Should().ContainSingle(w => w.Contains("Issues (write)") && w.Contains("cannot close the issues"));
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
        h.Queue.LastCheck.Title.Should().Be("Refused: role");
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

    /// <summary>§0 D69: S6 is settled (the App may push to a fork through "Allow edits by maintainers"), so a refusal is
    /// GitHub's own reason, shown as it is. Before, the message called fork pull requests unverified (S6).</summary>
    [Fact]
    public async Task A_fork_push_GitHub_refuses_is_evicted_with_GitHubs_reason()
    {
        var h = await HarnessAsync();
        h.Forge.PullRequests[Number] = PullRequest(fork: true, maintainerCanModify: true);
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(new GitWriterPublishResult(GitWriterOutcomes.PushRejected, null, null, [], "! [remote rejected] feature-7 (permission denied)", 10));

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.EvictionReason.Should().Be("fork-push-rejected");
        entry.EvictionMessage.Should().Be("GitHub refused the push to the fork's branch: ! [remote rejected] feature-7 (permission denied)");
        h.Writer.Publishes.Should().ContainSingle().Which.PushUrl.Should().Be("https://github.com/contributor/widget.git", "O3: the fork's branch");
    }

    /// <summary>
    /// §0 D69, D71: GitHub never lets an App create or update a workflow file a fork has on no branch, whatever the token
    /// asks for. A fork whose base changed its workflows therefore cannot be published by the queue until a person brings
    /// those files into the branch, which is what the eviction says, with its own reason. Before, it ended as
    /// <c>fork-push-rejected</c>, "unverified (S6)".
    /// </summary>
    [Fact]
    public async Task A_fork_publish_GitHub_refuses_for_workflow_files_asks_for_the_branch_to_be_updated()
    {
        var h = await HarnessAsync();
        h.Forge.PullRequests[Number] = PullRequest(fork: true, maintainerCanModify: true);
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared) with { BaseChangedWorkflows = true });
        h.Writer.PublishResults.Enqueue(new GitWriterPublishResult(GitWriterOutcomes.PushRejected, null, null, [],
            "!\t4444444444444444444444444444444444444444:refs/heads/feature-7\t[remote rejected] (refusing to allow a GitHub App to create or update "
            + "workflow `.github/workflows/ci.yml` without `workflows` permission) error: failed to push some refs to 'https://github.com/contributor/widget.git'", 10));

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("fork-workflow-update-needed");
        entry.EvictionMessage.Should().Contain("does not let an App write workflow files into a fork")
            .And.Contain("Update branch").And.Contain("queue it again");
        h.Queue.Comments.Should().ContainSingle().Which.Body.Should().Contain("Update branch");
        h.Queue.MergeRequests.Should().BeEmpty();
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

    // ── Dependency-updates R27 (S18-D3): the updater's own rolling pull request and the approval floor ──────────

    private const string BotAuthor = "coverageproduction[bot]";

    private static async Task<MergeQueueHarness> RollingPullRequestAsync(MergeQueueHarness h, string botSha)
    {
        h.Forge.PullRequests[Number] = PullRequest(author: BotAuthor) with { HeadRef = DependencyUpdateRun.RollingBranch };
        using var session = h.Store.OpenAsyncSession();
        await session.StoreAsync(new DependencyUpdateRun
        {
            Id = DependencyUpdateRun.ForRepository(RepositoryId), Repository = RepositoryId, PullRequestNumber = Number, RollingSha = Head, RollingBotSha = botSha,
        }, DependencyUpdateRun.ForRepository(RepositoryId));
        await session.SaveChangesAsync();
        return h;
    }

    [Fact]
    public async Task R27_the_bots_own_rolling_pull_request_passes_the_approval_floor_without_a_person()
    {
        var h = await RollingPullRequestAsync(await HarnessAsync(mergeBase: BaseSha), botSha: Head);

        await h.EnqueueAsync(DependencyUpdateEngine.QueueActor, EMergeQueueCommandSource.DependencyUpdates);
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged, "S18: refused before with \"@coverageproduction[bot] does not have write access\"");
    }

    [Fact]
    public async Task R27_a_human_commit_on_the_rolling_branch_needs_a_writers_approval_again()
    {
        // The head is a replayed human commit on top of the bot's (RollingSha != RollingBotSha).
        var h = await RollingPullRequestAsync(await HarnessAsync(mergeBase: BaseSha), botSha: "d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4");

        await h.EnqueueAsync(DependencyUpdateEngine.QueueActor, EMergeQueueCommandSource.DependencyUpdates);

        (await h.EntryAsync()).EvictionReason.Should().Be("approval");
    }

    [Fact]
    public async Task R27_a_writers_request_for_changes_still_vetoes_the_bots_pull_request()
    {
        var h = await RollingPullRequestAsync(await HarnessAsync(mergeBase: BaseSha), botSha: Head);
        h.Queue.Roles["reviewer"] = EForgeRepositoryRole.Write;
        h.Queue.Reviews.Add(new ForgeReview(1, "reviewer", EForgeReviewState.ChangesRequested, Head, null));

        await h.EnqueueAsync(DependencyUpdateEngine.QueueActor, EMergeQueueCommandSource.DependencyUpdates);

        (await h.EntryAsync()).EvictionReason.Should().Be("approval");
    }

    [Fact]
    public async Task R27_another_branch_with_the_bots_sha_is_not_trusted()
    {
        var h = await RollingPullRequestAsync(await HarnessAsync(mergeBase: BaseSha), botSha: Head);
        h.Forge.PullRequests[Number] = PullRequest(author: BotAuthor);

        await h.EnqueueAsync(DependencyUpdateEngine.QueueActor, EMergeQueueCommandSource.DependencyUpdates);

        (await h.EntryAsync()).EvictionReason.Should().Be("approval");
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
        // Something waits for acceptance (a permission the queue itself does not need, so the enqueue passes), and what the
        // App declares cannot be read, so nothing says the App itself lacks the permission.
        h.Permissions.Add(new FixedPermissions("https://github.com/organizations/acme/settings/installations/4242")
        {
            Pending = [new PendingPermission("pages", "Pages", "write", null)],
        });
        h.Queue.MergeThrows = new ForgeAccessDeniedException("GitHub answered 403 to PUT …/merge-async: Resource not accessible by integration", "contents=write");

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.EvictionReason.Should().Be("permission");
        entry.PermissionsUrl.Should().Be("https://github.com/organizations/acme/settings/installations/4242", "O17");
        h.Queue.LastCheck.Summary.Should().Contain("accept the new permissions");
    }

    /// <summary>
    /// §0 D96: the acceptance link only when something waits for acceptance. Live (2026-10-15) the eviction linked
    /// "Accept the App's new permissions" to an installation page with nothing pending.
    /// </summary>
    [Fact]
    public async Task A_refused_permission_with_nothing_pending_gives_no_acceptance_link()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Permissions.Add(new FixedPermissions("https://github.com/organizations/acme/settings/installations/4242"));
        h.Queue.MergeThrows = new ForgeAccessDeniedException("GitHub answered 403 to PUT …/merge-async: Resource not accessible by integration", "contents=write");

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.EvictionReason.Should().Be("permission");
        entry.PermissionsUrl.Should().BeNull();
        h.Queue.LastCheck.Summary.Should().NotContain("accept the new permissions");
        h.Queue.Comments.Should().NotContain(c => c.Body.Contains("Accept the App's new permissions"));
    }

    // ── §0 D95: what the App itself declares, checked at enqueue ─────────────────────────────────

    /// <summary>
    /// Live, 2026-10-15: the production App requested neither <c>actions</c> nor <c>statuses</c> and heard no
    /// <c>workflow_run</c>. The queue pushed its branch and learned of it from a 403 on the regenerate dispatch. Now the
    /// enqueue refuses before anything is prepared or pushed, and says only the App's owner can fix it, where.
    /// </summary>
    [Fact]
    public async Task An_app_that_does_not_declare_what_the_configuration_needs_refuses_before_anything_is_pushed()
    {
        var h = await HarnessAsync();
        var app = FixedPermissions.FullApp();
        app.Remove("actions");
        h.Permissions.Add(new FixedPermissions("https://github.com/organizations/acme/settings/installations/4242")
        {
            AppPermissions = app,
            AppEvents = [.. FixedPermissions.AllEvents.Where(e => e != "workflow_run")],
        });

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Refused);
        entry.EvictionReason.Should().Be("app-permissions");
        entry.EvictionMessage.Should().Contain("Actions (write)").And.Contain("Workflow run")
            .And.Contain(FixedPermissions.SettingsUrl).And.Contain("cannot add them");
        entry.PermissionsUrl.Should().BeNull("accepting on the installation cannot add what the App does not request");
        h.Writer.Prepares.Should().BeEmpty();
        h.Writer.QueueBranchPushes.Should().BeEmpty();
        h.Queue.Dispatches.Should().BeEmpty();
        h.Queue.LastCheck.Actions.Should().NotBeEmpty("the button stays: once the App is fixed, the next click is accepted");
    }

    [Fact]
    public async Task The_same_gaps_pass_a_configuration_that_dispatches_nothing_and_reruns_nothing()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        // No workflow, no take-base rule (which needs one), no reruns: nothing in this configuration needs Actions.
        h.Forge.Files["master/coverage.yml"] = """
            mergeQueue:
              enabled: true
              branches: [master]
              enqueue:
                label: merge-queue
              keepBothDefaults: false
              checks:
                retryFlaky: 0
            """;
        var app = FixedPermissions.FullApp();
        app.Remove("actions");
        h.Permissions.Add(new FixedPermissions("https://github.com/organizations/acme/settings/installations/4242")
        {
            AppPermissions = app,
            AppEvents = [.. FixedPermissions.AllEvents.Where(e => e != "workflow_run")],
        });

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.EvictionReason.Should().NotBe("app-permissions");
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
    }

    [Fact]
    public async Task An_app_without_statuses_is_refused()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        var app = FixedPermissions.FullApp();
        app.Remove("statuses");
        h.Permissions.Add(new FixedPermissions("https://github.com/organizations/acme/settings/installations/4242") { AppPermissions = app });

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Refused);
        entry.EvictionReason.Should().Be("app-permissions");
        entry.EvictionMessage.Should().Contain("Statuses (read)");
    }

    [Fact]
    public async Task An_app_declaration_that_cannot_be_read_fails_open()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        h.Permissions.Add(new FixedPermissions("https://github.com/organizations/acme/settings/installations/4242") { AppPermissions = null });

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
    }

    /// <summary>After the App is updated, before the installation accepts: the needed permission waits, so the link helps.</summary>
    [Fact]
    public async Task A_needed_permission_still_waiting_for_acceptance_refuses_with_the_acceptance_link()
    {
        var h = await HarnessAsync();
        h.Permissions.Add(new FixedPermissions("https://github.com/organizations/acme/settings/installations/4242")
        {
            AppPermissions = FixedPermissions.FullApp(),
            Pending = [new PendingPermission("actions", "Actions", "write", null)],
        });

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Refused);
        entry.EvictionReason.Should().Be("permission");
        entry.EvictionMessage.Should().Contain("Actions (write)");
        entry.PermissionsUrl.Should().Be("https://github.com/organizations/acme/settings/installations/4242");
        h.Writer.Prepares.Should().BeEmpty();
    }

    /// <summary>
    /// <c>workflows</c> is taken only by a push that touches a workflow file (§0 D59): its absence warns at enqueue, and
    /// a 403 asking for it evicts naming the App's settings page, not the installation's.
    /// </summary>
    [Fact]
    public async Task An_app_without_workflows_warns_and_a_403_for_it_points_at_the_apps_settings()
    {
        var h = await HarnessAsync(mergeBase: BaseSha);
        var app = FixedPermissions.FullApp();
        app.Remove("workflows");
        h.Permissions.Add(new FixedPermissions("https://github.com/organizations/acme/settings/installations/4242")
        {
            AppPermissions = app,
            Pending = [new PendingPermission("pages", "Pages", "write", null)],
        });
        h.Queue.MergeThrows = new ForgeAccessDeniedException("GitHub answered 403: Resource not accessible by integration", "workflows=write");

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.Warnings.Should().ContainSingle(w => w.Contains("Workflows (write)"));
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("app-permissions");
        entry.EvictionMessage.Should().Contain("does not request Workflows (write)").And.Contain(FixedPermissions.SettingsUrl);
        entry.PermissionsUrl.Should().BeNull();
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
        h.Queue.Dispatches.Should().BeEmpty();
        // §0 D59: the change touches .github/workflows/**, so the publish needs GitHub's workflow permission. The prepare
        // pushes nothing (§0 D65) and needs none, and without a dispatch there is no queue branch to delete.
        h.Queue.WriterAccesses.Select(a => a.Workflows).ToList().Should().Equal([false, true]);
        h.Writer.QueueBranchPushes.Should().BeEmpty();
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

    /// <summary>
    /// O25: without a dispatch, a fork's change under .github/** goes through like a same-repository one (O21 A). Since §0
    /// D65 nothing of the pull request reaches this repository before the merge: the rebased commit goes to the fork's own
    /// branch, where this repository's pull_request workflows run it without secrets, as they would without the queue.
    /// The publish's token does not ask for the workflow permission, which GitHub ignores for a fork's branch (§0 D69,
    /// D71; it asked under D59 and D67). Before O25 the entry was evicted touches-workflows.
    /// </summary>
    [Fact]
    public async Task A_forks_change_under_github_that_needs_no_regeneration_is_published_to_the_fork_and_merged()
    {
        var h = await HarnessAsync();
        h.Forge.PullRequests[Number] = PullRequest(fork: true, maintainerCanModify: true);
        h.Writer.PrepareResults.Enqueue(TouchesWorkflows(FakeGitWriter.Prepared(BaseSha, Head, Prepared), ".github/workflows/ci.yml"));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.History.Should().Contain(e => e.Text.Contains("its .github/** change goes through, as no workflow will be dispatched", StringComparison.Ordinal));
        h.Writer.Publishes.Single().PushUrl.Should().Be("https://github.com/contributor/widget.git", "O3: the fork's branch");
        h.Writer.QueueBranchPushes.Should().BeEmpty("nothing of the fork reaches a branch of this repository");
        h.Queue.Dispatches.Should().BeEmpty();
        h.Queue.WorkflowFileReads.Should().BeEmpty("without a dispatch there is no fork precondition to read");
        h.Queue.WriterAccesses.Select(a => (a.Number, a.Workflows)).ToList().Should().Equal([((int?)null, false), (Number, false)]);
    }

    /// <summary>
    /// O25: a fork's change under .github/** that needs the regenerate workflow ends before anything is pushed. The
    /// queue-branch push would put the fork's workflow files on a branch of this repository, where its push workflows
    /// run them with its secrets. Its own reason, so the check run and the guide can say no role bypasses it.
    /// </summary>
    [Fact]
    public async Task A_forks_change_under_github_that_needs_regeneration_is_evicted_before_anything_is_pushed()
    {
        var h = await HarnessAsync();
        h.Forge.PullRequests[Number] = PullRequest(fork: true, maintainerCanModify: true);
        h.Writer.PrepareResults.Enqueue(TouchesWorkflows(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"), ".github/workflows/ci.yml"));

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("fork-touches-workflows");
        entry.EvictionPaths.Should().Equal([".github/workflows/ci.yml"]);
        entry.EvictionMessage.Should().Contain("fork");
        entry.EvictionMessage.Should().Contain("mq/master/pr-7");
        entry.EvictionMessage.Should().Contain("no role");
        h.Writer.QueueBranchPushes.Should().BeEmpty();
        h.Queue.Dispatches.Should().BeEmpty();
        h.Writer.Publishes.Should().BeEmpty();
        h.Queue.WorkflowFileReads.Should().BeEmpty("the plan ends it before the fork precondition is read");
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

    /// <summary>O25, O22: a fork's .github/** change that needs the regenerate workflow is evicted whoever queued it; the
    /// bypass role lets only a same-repository one through.</summary>
    [Fact]
    public async Task The_bypass_role_never_lets_a_forks_github_change_through()
    {
        var h = await HarnessAsync();
        await SetBypassRoleAsync(h, EMergeQueueBypassRole.Maintain);
        h.Queue.Roles["owner"] = EForgeRepositoryRole.Admin;
        h.Forge.PullRequests[Number] = PullRequest(fork: true, maintainerCanModify: true);
        RegeneratedGitHubChange(h);

        await h.EnqueueAsync("owner");
        await h.DrainAsync();

        (await h.EntryAsync()).EvictionReason.Should().Be("fork-touches-workflows", "T5: a fork's workflow files never reach mq/, whoever queued them");
        h.Writer.QueueBranchPushes.Should().BeEmpty();
        h.Queue.Dispatches.Should().BeEmpty();
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
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "completed", "success", Prepared, "mq/master/pr-7", null);
        h.Queue.Artifacts[9001] = new ForgeArtifactDownload(EForgeArtifactOutcome.Ok, "patch.diff", [], null);

        await h.EnqueueAsync();
        await h.DrainAsync();
        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9001);
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        h.Queue.WriterAccesses.Should().Equal([((int?)null, false, "job-token-1"), (null, false, "job-token-2"), (Number, false, "job-token-3"), (null, false, "job-token-4")]);
        h.Writer.Prepares.Single().Credential.Password.Should().Be("job-token-1");
        h.Writer.QueueBranchPushes.Single().Prepare.Credential.Password.Should().Be("job-token-2", "§0 D65: the queue-branch push is a job of its own");
        h.Writer.Publishes.Single().Prepare.Credential.Password.Should().Be("job-token-3");
        h.Writer.Deletes.Single().Credential.Password.Should().Be("job-token-4", "the queue branch's deletion gets its own too");
        h.Queue.RevokedTokens.Should().Equal(["job-token-1", "job-token-2", "job-token-3", "job-token-4"]);
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

    /// <summary>The queue branch's cleanup runs after the entry ended: a mint GitHub refuses is refused on every retry, so
    /// the step logs it and leaves the branch, instead of throwing into the messaging runtime's retries.</summary>
    [Fact]
    public async Task A_cleanup_whose_credential_is_refused_leaves_the_branch_without_failing_the_step()
    {
        var h = await HarnessAsync();
        // A dispatch, so there is a queue branch to delete (§0 D65): prepare, queue-branch push, publish, then the cleanup.
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "completed", "success", Prepared, "mq/master/pr-7", null);
        h.Queue.Artifacts[9001] = new ForgeArtifactDownload(EForgeArtifactOutcome.Ok, "patch.diff", [], null);
        var minted = 0;
        h.Queue.WriterAccess = _ => ++minted == 4
            ? throw new ForgeAccessDeniedException("GitHub refused a token: Resource not accessible by integration", "contents=write")
            : null;

        await h.EnqueueAsync();
        await h.DrainAsync();
        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9001);
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        h.Writer.Deletes.Should().BeEmpty();
        h.Handled.Should().Contain(s => s.Step == EMergeQueueStep.Cleanup);
    }

    /// <summary>
    /// §0 D59, D69, D71: a same-repository publish's token asks for the workflow permission exactly when its own change
    /// touches <c>.github/workflows/**</c> (the base's workflow files are on the base branch already). A fork's never does:
    /// GitHub evaluates the permission against the fork, where the base's installation holds none, so it changes nothing
    /// there (T9, least privilege). Before D69, a fork's asked when the base changed its workflows or its own change touched
    /// them. A file under <c>.github/</c> but outside <c>workflows/</c> needs nothing.
    /// </summary>
    [Theory]
    [InlineData(true, true, null, false)]
    [InlineData(true, false, ".github/workflows/ci.yml", false)]
    [InlineData(true, false, ".github/dependabot.yml", false)]
    [InlineData(true, false, null, false)]
    [InlineData(false, true, null, false)]
    [InlineData(false, false, ".github/workflows/ci.yml", true)]
    public async Task A_publish_asks_for_the_workflow_permission_only_for_a_same_repository_change_to_the_workflows(
        bool fork, bool baseChangedWorkflows, string? ownChange, bool workflows)
    {
        var h = await HarnessAsync();
        h.Forge.PullRequests[Number] = PullRequest(fork: fork, maintainerCanModify: true);
        var prepared = FakeGitWriter.Prepared(BaseSha, Head, Prepared) with { BaseChangedWorkflows = baseChangedWorkflows };
        h.Writer.PrepareResults.Enqueue(ownChange is null ? prepared : TouchesWorkflows(prepared, ownChange));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        h.Queue.WriterAccesses.Select(a => (a.Number, a.Workflows)).ToList().Should().Equal([((int?)null, false), (Number, workflows)]);
        h.Writer.Publishes.Single().PushUrl.Should().Be(fork ? "https://github.com/contributor/widget.git" : null);
    }

    // ── §0 D65: the queue branch only for a dispatch, and a fork's only when nothing there runs its code ─────

    /// <summary>A fork's pull request whose prepare resolved a generated file, so it needs the regenerate workflow.</summary>
    private async Task<MergeQueueHarness> ForkNeedingRegenerationAsync()
    {
        var h = await HarnessAsync();
        h.Forge.PullRequests[Number] = PullRequest(fork: true, maintainerCanModify: true);
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"));
        return h;
    }

    /// <summary>
    /// The pwn-request §0 D65 closes. Before, the prepare pushed the fork's commit to <c>mq/master/pr-7</c> in the base
    /// repository and the saga dispatched, whatever the base's <c>push</c> workflows were: an unfiltered <c>on: push</c>
    /// would run the fork's code on that branch with the base repository's secrets (red at 6fa6acb3: dispatched).
    /// </summary>
    [Fact]
    public async Task A_forks_dispatch_is_refused_before_anything_is_pushed_when_a_push_workflow_would_run_on_the_queue_branch()
    {
        var h = await ForkNeedingRegenerationAsync();
        h.Queue.WorkflowFiles[BaseSha].Add(new(".github/workflows/ci.yml", "name: ci\non: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: make test\n"));

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("fork-unsafe-workflows");
        entry.EvictionPaths.Should().Equal([".github/workflows/ci.yml"]);
        entry.EvictionMessage.Should().Contain("`.github/workflows/ci.yml` on `master`, line 2");
        entry.EvictionMessage.Should().Contain("mq/master/pr-7");
        entry.EvictionMessage.Should().Contain("branches-ignore: ['mq/**']");
        h.Queue.WorkflowFileReads.Should().Equal([BaseSha, "master"], "the push triggers are the base commit's, the rest the default branch's");
        h.Writer.QueueBranchPushes.Should().BeEmpty("the fork's code never reaches a branch of this repository");
        h.Queue.Dispatches.Should().BeEmpty();
        entry.QueueBranchSha.Should().BeNull();
        h.Writer.Deletes.Should().BeEmpty("nothing was pushed, so nothing is deleted");
    }

    [Fact]
    public async Task A_forks_dispatch_goes_ahead_when_no_workflow_runs_on_the_queue_branch_and_the_regenerate_workflow_is_read_only()
    {
        var h = await ForkNeedingRegenerationAsync();
        h.Queue.WorkflowFiles[BaseSha].Add(new(".github/workflows/ci.yml", "on:\n  push:\n    branches-ignore: ['mq/**']\n  pull_request:\n"));
        h.Queue.WorkflowFiles[BaseSha].Add(new(".github/workflows/release.yml", "on:\n  push:\n    tags: ['v*']\n"));
        h.Queue.WorkflowFiles[BaseSha].Add(new(".github/workflows/deploy.yml", "on:\n  push:\n    branches: [master]\n    paths: ['src/**']\n"));

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Regenerating);
        entry.History.Should().Contain(e => e.Text.StartsWith("the fork precondition holds", StringComparison.Ordinal));
        var push = h.Writer.QueueBranchPushes.Should().ContainSingle().Which;
        push.PreparedSha.Should().Be(Prepared);
        push.HeadFromFork.Should().BeTrue("O25, §0 D67: the writer refuses a fork's .github change on its own");
        h.Queue.Dispatches.Should().ContainSingle();
        entry.QueueBranchSha.Should().Be(Prepared);
    }

    [Theory]
    [InlineData("secrets", "      - run: echo ${{ secrets.NPM_TOKEN }}\n")]
    [InlineData("permissions", "    permissions:\n      contents: write\n")]
    [InlineData("self-hosted", "    runs-on: [self-hosted, linux]\n")]
    public async Task A_forks_dispatch_is_refused_when_the_regenerate_workflow_is_not_read_only(string what, string line)
    {
        var h = await ForkNeedingRegenerationAsync();
        // The raw literal carries the source file's line endings (CRLF in a Windows checkout), and the replacements match
        // "\n": without normalising, they silently match nothing and the workflow stays safe.
        var safe = SafeRegenerate.ReplaceLineEndings("\n");
        var regenerate = what switch
        {
            "secrets" => safe + "\n" + line,
            "permissions" => safe.Replace("    runs-on: ubuntu-latest\n", "    runs-on: ubuntu-latest\n" + line, StringComparison.Ordinal),
            _ => safe.Replace("    runs-on: ubuntu-latest\n", line, StringComparison.Ordinal),
        };
        regenerate.Should().NotBe(safe, "the unsafe line must actually be in the workflow");
        h.Queue.WorkflowFiles["master"] = [new(RegeneratePath, regenerate)];

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("fork-unsafe-workflows");
        entry.EvictionMessage.Should().Contain("`.github/workflows/regenerate.yml` on `master`");
        h.Writer.QueueBranchPushes.Should().BeEmpty();
        h.Queue.Dispatches.Should().BeEmpty();
    }

    /// <summary>A fork's pull request that needs no regeneration publishes to the fork and never touches the base
    /// repository's branches, so there is nothing to check (§0 D65).</summary>
    [Fact]
    public async Task A_fork_that_needs_no_dispatch_is_published_without_a_queue_branch_or_a_workflow_check()
    {
        var h = await HarnessAsync();
        h.Forge.PullRequests[Number] = PullRequest(fork: true, maintainerCanModify: true);
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));
        h.Queue.WorkflowFiles[BaseSha].Add(new(".github/workflows/ci.yml", "on: push\n"));

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        h.Writer.Publishes.Single().PushUrl.Should().Be("https://github.com/contributor/widget.git");
        h.Writer.QueueBranchPushes.Should().BeEmpty();
        h.Queue.WorkflowFileReads.Should().BeEmpty("nothing is pushed to this repository, so no workflow here can run the fork's code");
        h.Writer.Deletes.Should().BeEmpty();
    }

    /// <summary>
    /// O25: a check, a commit status or a deployment of the queue branch's commit starts these workflows with this
    /// repository's secrets, and such a workflow often checks out the commit the event names: the fork's code. The fork
    /// precondition reads them at the base commit and on the default branch, whatever their activity types.
    /// </summary>
    [Theory]
    [InlineData("check_run", true)]
    [InlineData("check_suite", false)]
    [InlineData("status", true)]
    [InlineData("deployment_status", false)]
    public async Task A_forks_dispatch_is_refused_while_a_workflow_runs_on_the_queue_commits_checks_statuses_or_deployments(string trigger, bool atTheBaseCommit)
    {
        var h = await ForkNeedingRegenerationAsync();
        h.Queue.WorkflowFiles[atTheBaseCommit ? BaseSha : "master"].Add(new(".github/workflows/notify.yml",
            $"name: notify\non:\n  {trigger}:\n    types: [completed]\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n"));

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("fork-unsafe-workflows");
        entry.EvictionPaths.Should().Equal([".github/workflows/notify.yml"]);
        entry.EvictionMessage.Should().Contain($"`.github/workflows/notify.yml` on `master`, line 3: `on: {trigger}`");
        entry.EvictionMessage.Should().Contain("check out");
        h.Writer.QueueBranchPushes.Should().BeEmpty();
        h.Queue.Dispatches.Should().BeEmpty();
    }

    /// <summary>
    /// O25, §0 D67: the git writer refuses to push a fork's .github/** change to the queue branch on its own, whatever the
    /// saga asked. Its refusal ends the entry with the plan's reason; nothing is dispatched.
    /// </summary>
    [Fact]
    public async Task A_queue_branch_push_the_writer_refuses_for_a_forks_github_change_evicts_without_dispatching()
    {
        var h = await ForkNeedingRegenerationAsync();
        h.Writer.QueueBranchResults.Enqueue(new GitWriterQueueBranchResult(GitWriterOutcomes.ForkTouchesWorkflows, null, null, [".github/workflows/ci.yml"],
            "The pull request's head is a fork's, and the commit changes .github/ against its base: mq/master/pr-7 is a branch of this repository.", 10));

        await h.EnqueueAsync();
        await h.DrainAsync();

        h.Writer.QueueBranchPushes.Should().ContainSingle().Which.HeadFromFork.Should().BeTrue();
        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("fork-touches-workflows");
        entry.EvictionPaths.Should().Equal([".github/workflows/ci.yml"]);
        entry.EvictionMessage.Should().Contain("git writer");
        h.Queue.Dispatches.Should().BeEmpty();
        entry.QueueBranchSha.Should().BeNull();
        h.Writer.Deletes.Should().BeEmpty("nothing was pushed, so nothing is deleted");
    }

    /// <summary>§0 D65: the queue-branch push carries the commit the saga holds or nothing; a rebuild that differs would
    /// differ again, so it is not retried.</summary>
    [Fact]
    public async Task A_queue_branch_push_whose_rebuild_differs_evicts_without_dispatching()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"));
        h.Writer.QueueBranchResults.Enqueue(new GitWriterQueueBranchResult(GitWriterOutcomes.PreparedDiffers, null, null, [],
            $"The prepared commit rebuilt from this request is {Final}, not {Prepared}.", 10));

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("prepared-differs");
        entry.TransientFailures.Should().Be(0);
        h.Writer.QueueBranchPushes.Should().ContainSingle();
        h.Queue.Dispatches.Should().BeEmpty();
        h.Writer.Deletes.Should().BeEmpty();
    }

    /// <summary>A queue branch that holds something else (a run whose cleanup has not run yet) is reset, and the run
    /// prepares again; the next push leases on "absent".</summary>
    [Fact]
    public async Task A_queue_branch_out_of_step_is_reset_and_the_run_prepares_again()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"));
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, "apps/A/App_Data/modelHashes.json"));
        h.Writer.QueueBranchResults.Enqueue(new GitWriterQueueBranchResult(GitWriterOutcomes.QueueBranchMoved, null, null, [], "mq/master/pr-7 is not where the lease expected it.", 10));

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Regenerating);
        h.Writer.Deletes.Should().ContainSingle().Which.ExpectedSha.Should().BeNull("the reset removes whatever is there");
        h.Writer.Prepares.Should().HaveCount(2);
        h.Writer.QueueBranchPushes.Select(p => p.ExpectedQueueSha).Should().Equal([null, null]);
        entry.QueueBranchSha.Should().Be(Prepared);
        h.Queue.Dispatches.Should().ContainSingle();
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

    /// <summary>
    /// The engine's remark (D11): its session runs with optimistic concurrency, so a handler whose entry another writer
    /// changed meanwhile, such as a second host, fails its save instead of overwriting that change. §0 D74 replaced the
    /// obsolete <c>UseOptimisticConcurrency = true</c> with <c>OptimisticConcurrencyMode.Writes</c>, which RavenDB 7.2
    /// documents as the same setting; this holds it, and was green before and after.
    /// </summary>
    [Fact]
    public async Task A_step_whose_entry_another_writer_changed_fails_its_save_instead_of_overwriting_the_change()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));
        h.WriterOverride = new RacingWriter(h.Writer, h.Store, MergeQueueEntry.DocumentId(RepositoryId, Number));
        await h.EnqueueAsync();

        var step = async () => await h.DrainAsync();

        await step.Should().ThrowAsync<Raven.Client.Exceptions.ConcurrencyException>();
        var entry = await h.EntryAsync();
        entry.Title.Should().Be("Changed by another writer");
        entry.State.Should().Be(EMergeQueueEntryState.Preparing, "the step's own changes were not saved");
    }

    /// <summary>The scripted writer, with another writer of the entry while the prepare runs.</summary>
    private sealed class RacingWriter(FakeGitWriter inner, Raven.Client.Documents.IDocumentStore store, string entryId) : IGitWriterSidecar
    {
        public bool IsConfigured => inner.IsConfigured;

        public async Task<GitWriterPrepareResult> PrepareAsync(GitWriterPrepareRequest request, CancellationToken cancellationToken = default)
        {
            using (var session = store.OpenAsyncSession())
            {
                var entry = await session.LoadAsync<MergeQueueEntry>(entryId, cancellationToken);
                entry!.Title = "Changed by another writer";
                await session.SaveChangesAsync(cancellationToken);
            }
            return await inner.PrepareAsync(request, cancellationToken);
        }

        public Task<GitWriterQueueBranchResult> PushQueueBranchAsync(GitWriterQueueBranchRequest request, CancellationToken cancellationToken = default)
            => inner.PushQueueBranchAsync(request, cancellationToken);

        public Task<GitWriterPublishResult> PublishAsync(GitWriterPublishRequest request, CancellationToken cancellationToken = default)
            => inner.PublishAsync(request, cancellationToken);

        public Task<GitWriterDeleteResult> DeleteQueueBranchAsync(GitWriterDeleteQueueBranchRequest request, CancellationToken cancellationToken = default)
            => inner.DeleteQueueBranchAsync(request, cancellationToken);
    }

    /// <summary>
    /// The installation's pending permissions and the App's declaration, scripted; the gaps and the needed pending ones are
    /// computed by the GitHub implementation's own pure functions (§0 D95).
    /// </summary>
    private sealed class FixedPermissions(string url) : IForgeInstallationPermissions
    {
        public const string SettingsUrl = "https://github.com/organizations/acme/settings/apps/coverage/permissions";

        /// <summary>Everything the queue can need: what a correctly set-up App declares.</summary>
        public static Dictionary<string, string> FullApp() => new()
        {
            ["contents"] = "write", ["pull_requests"] = "write", ["checks"] = "write", ["metadata"] = "read",
            ["statuses"] = "read", ["issues"] = "write", ["actions"] = "write", ["workflows"] = "write",
        };

        public static readonly string[] AllEvents =
            ["pull_request", "push", "check_run", "check_suite", "status", "pull_request_review", "issue_comment", "workflow_run"];

        public PendingPermission[] Pending { get; init; } = [];

        // What the App declares; null: it could not be read (fail open). A plain comment: Spark's attribute-description
        // generator reads a property's doc comment, and this class is private.
        public Dictionary<string, string>? AppPermissions { get; init; }

        public string[] AppEvents { get; init; } = AllEvents;

        public EForgeProvider Provider => EForgeProvider.GitHub;

        public Task<PendingInstallationPermissions?> GetPendingAsync(Account account, CancellationToken cancellationToken = default)
            => Task.FromResult<PendingInstallationPermissions?>(new PendingInstallationPermissions(url, Pending));

        public Task<ForgeAppGaps?> GetAppGapsAsync(EMergeQueueAppNeed needs, CancellationToken cancellationToken = default)
            => Task.FromResult(AppPermissions is null
                ? null
                : GitHubInstallationPermissions.ComputeMissing(AppPermissions, AppEvents.ToHashSet(), needs) with { SettingsUrl = SettingsUrl });

        public IReadOnlyList<PendingPermission> NeededPending(IReadOnlyList<PendingPermission> pending, EMergeQueueAppNeed needs)
            => GitHubInstallationPermissions.NeededPendingOf(pending, needs);
    }
}
