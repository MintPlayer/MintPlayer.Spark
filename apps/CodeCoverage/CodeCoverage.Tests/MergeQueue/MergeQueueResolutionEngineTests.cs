using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using Xunit;
using static CodeCoverage.Tests.MergeQueue.MergeQueueHarness;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// Conflict resolution v2 in the saga (merge-queue PRD §10.5, §10.6, O35 #4, O36): which resolutions dispatch, several
/// regenerate workflows one after the other, their patches published together, a writer whose resolver changed, and
/// documents written before O36.
/// </summary>
public class MergeQueueResolutionEngineTests : CoverageRavenTest
{
    private const string Security = "apps/A/App_Data/security.json";
    private const string ModelHashes = "apps/A/App_Data/modelHashes.json";
    private const string LockFile = "apps/A/packages.lock.json";

    private const string KeepBothYml = """
        mergeQueue:
          enabled: true
          branches: [master]
          postRebaseActionName: regenerate.yml
          regeneratedPaths: ["**/App_Data/modelHashes.json"]
          autoResolve:
            - path: "**/App_Data/modelHashes.json"
              strategy: take-base
            - path: "**/security.json"
              strategy: keep-both
              validate: [json-no-duplicate-keys]
              regenerate: false
          keepBothDefaults: false
        """;

    private const string TwoWorkflowsYml = """
        mergeQueue:
          enabled: true
          branches: [master]
          postRebaseActionName: regenerate.yml
          regeneratedPaths: ["**/App_Data/modelHashes.json"]
          autoResolve:
            - path: "**/App_Data/modelHashes.json"
              strategy: take-base
            - path: "**/packages.lock.json"
              strategy: take-base
              action: lockfiles.yml
          keepBothDefaults: false
        """;

    private async Task<MergeQueueHarness> HarnessAsync(string? yml = null)
    {
        var harness = new MergeQueueHarness(GetDocumentStore());
        if (yml is not null)
            harness.Forge.Files[$"master/{CodeCoverage.Feedback.CoverageYml.FileName}"] = yml;
        await harness.SeedAsync();
        harness.Queue.Blobs["master:.github/workflows/lockfiles.yml"] = "10cc";
        harness.Queue.Blobs[$"{Prepared}:.github/workflows/lockfiles.yml"] = "10cc";
        return harness;
    }

    private static GitWriterPrepareResult PreparedWith(params GitWriterResolution[] resolutions)
        => FakeGitWriter.Prepared(BaseSha, Head, Prepared, [.. resolutions.Select(r => r.Path)]) with { Resolutions = resolutions, ResolverVersion = "resolution-2; git version 2.50.1" };

    [Fact]
    public async Task A_keep_both_only_resolution_publishes_without_a_dispatch_or_a_queue_branch()
    {
        var h = await HarnessAsync(KeepBothYml);
        h.Writer.PrepareResults.Enqueue(PreparedWith(new GitWriterResolution(Security, "keep-both", false, 1)));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.RegenerateWorkflows.Should().BeEmpty();
        entry.Resolutions.Should().ContainSingle().Which.Strategy.Should().Be("keep-both");
        h.Queue.Dispatches.Should().BeEmpty("keep-both with regenerate: false needs no workflow (§10.5)");
        h.Writer.QueueBranchPushes.Should().BeEmpty("nothing is dispatched, so nothing is pushed to the queue branch (§0 D65)");
        h.Writer.Publishes.Should().ContainSingle().Which.Patch.Should().BeNull();
        // The rule reaches the writer with its strategy and validators.
        var wire = h.Writer.Prepares.Single().AutoResolve;
        wire[1].Strategy.Should().Be("keep-both");
        wire[1].Validate.Should().Equal(["json-no-duplicate-keys"]);
        wire[1].Regenerate.Should().Be(false);
    }

    [Fact]
    public async Task A_take_base_beside_a_keep_both_dispatches()
    {
        var h = await HarnessAsync(KeepBothYml);
        h.Writer.PrepareResults.Enqueue(PreparedWith(new GitWriterResolution(ModelHashes, "take-base", true, 0), new GitWriterResolution(Security, "keep-both", false, 1)));

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Regenerating);
        h.Queue.Dispatches.Should().ContainSingle().Which.Workflow.Should().Be("regenerate.yml");
    }

    [Fact]
    public async Task A_writer_that_does_not_say_how_it_resolved_dispatches_as_before()
    {
        var h = await HarnessAsync(KeepBothYml);
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, Security));

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.Resolutions.Should().BeNull();
        entry.State.Should().Be(EMergeQueueEntryState.Regenerating, "without resolutions every resolved path counts as regenerating (§10.6)");
        h.Queue.Dispatches.Should().ContainSingle().Which.Workflow.Should().Be("regenerate.yml");
    }

    [Fact]
    public async Task Two_rules_with_two_workflows_dispatch_one_after_the_other_and_publish_both_patches()
    {
        var h = await HarnessAsync(TwoWorkflowsYml);
        h.Writer.PrepareResults.Enqueue(PreparedWith(new GitWriterResolution(ModelHashes, "take-base", true, 0), new GitWriterResolution(LockFile, "take-base", true, 1)));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));
        h.Queue.DispatchQueue.Enqueue(new(EForgeDispatchOutcome.Dispatched, 9001, "https://github.com/acme/widget/actions/runs/9001", null));
        h.Queue.DispatchQueue.Enqueue(new(EForgeDispatchOutcome.Dispatched, 9002, "https://github.com/acme/widget/actions/runs/9002", null));
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "completed", "success", Prepared, "mq/master/pr-7", null);
        h.Queue.Runs[9002] = new ForgeWorkflowRun(9002, "completed", "success", Prepared, "mq/master/pr-7", null);
        h.Queue.Artifacts[9001] = new ForgeArtifactDownload(EForgeArtifactOutcome.Ok, "patch.diff", "diff --git a/l b/l\n"u8.ToArray(), null);
        h.Queue.Artifacts[9002] = new ForgeArtifactDownload(EForgeArtifactOutcome.Ok, "patch.diff", "diff --git a/m b/m\n"u8.ToArray(), null);

        await h.EnqueueAsync();
        await h.DrainAsync();

        var first = await h.EntryAsync();
        first.RegenerateWorkflows.Should().Equal(["lockfiles.yml", "regenerate.yml"]);
        h.Queue.Dispatches.Should().ContainSingle().Which.Workflow.Should().Be("lockfiles.yml");

        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9001);
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Regenerating);
        h.Queue.Dispatches.Select(d => d.Workflow).Should().Equal(["lockfiles.yml", "regenerate.yml"]);
        h.Queue.Dispatches.Should().AllSatisfy(d => d.Inputs["expected_sha"].Should().Be(Prepared, "every run builds the same prepared commit"));
        h.Writer.QueueBranchPushes.Should().ContainSingle("the queue branch is written once, before the first dispatch");

        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9002);
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.Dispatches.Select(d => (d.Workflow, d.RunId, d.Succeeded)).Should().Equal([("lockfiles.yml", (long?)9001, true), ("regenerate.yml", (long?)9002, true)]);
        var publish = h.Writer.Publishes.Should().ContainSingle().Which;
        publish.Patch.Should().BeNull("several runs send their patches apart");
        publish.Patches!.Select(p => p.Workflow).Should().Equal(["lockfiles.yml", "regenerate.yml"]);
        publish.Patches![0].Patch.Should().Be(Convert.ToBase64String("diff --git a/l b/l\n"u8.ToArray()));
        h.Queue.CancelledRuns.Should().BeEmpty();
    }

    [Fact]
    public async Task Patches_that_overlap_evict_regenerate_overlap()
    {
        var h = await HarnessAsync(TwoWorkflowsYml);
        h.Writer.PrepareResults.Enqueue(PreparedWith(new GitWriterResolution(ModelHashes, "take-base", true, 0), new GitWriterResolution(LockFile, "take-base", true, 1)));
        h.Writer.PublishResults.Enqueue(new GitWriterPublishResult(GitWriterOutcomes.PatchRejected, GitWriterReasons.Overlap, null, [ModelHashes], "Two regenerate workflows changed this path differently.", 5));
        h.Queue.DispatchQueue.Enqueue(new(EForgeDispatchOutcome.Dispatched, 9001, null, null));
        h.Queue.DispatchQueue.Enqueue(new(EForgeDispatchOutcome.Dispatched, 9002, null, null));
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "completed", "success", Prepared, "mq/master/pr-7", null);
        h.Queue.Runs[9002] = new ForgeWorkflowRun(9002, "completed", "success", Prepared, "mq/master/pr-7", null);
        h.Queue.Artifacts[9001] = new ForgeArtifactDownload(EForgeArtifactOutcome.Ok, "patch.diff", "diff --git a/x b/x\n"u8.ToArray(), null);
        h.Queue.Artifacts[9002] = new ForgeArtifactDownload(EForgeArtifactOutcome.Ok, "patch.diff", "diff --git a/x b/x\n"u8.ToArray(), null);

        await h.EnqueueAsync();
        await h.DrainAsync();
        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9001);
        await h.DrainAsync();
        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9002);
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("regenerate-overlap");
        entry.EvictionPaths.Should().Equal([ModelHashes]);
    }

    [Fact]
    public async Task A_failing_second_workflow_evicts_and_a_timeout_cancels_the_running_one()
    {
        var h = await HarnessAsync(TwoWorkflowsYml);
        h.Writer.PrepareResults.Enqueue(PreparedWith(new GitWriterResolution(ModelHashes, "take-base", true, 0), new GitWriterResolution(LockFile, "take-base", true, 1)));
        h.Queue.DispatchQueue.Enqueue(new(EForgeDispatchOutcome.Dispatched, 9001, null, null));
        h.Queue.DispatchQueue.Enqueue(new(EForgeDispatchOutcome.Dispatched, 9002, null, null));
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "completed", "success", Prepared, "mq/master/pr-7", null);
        h.Queue.Runs[9002] = new ForgeWorkflowRun(9002, "in_progress", null, Prepared, "mq/master/pr-7", null);

        await h.EnqueueAsync();
        await h.DrainAsync();
        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9001);
        await h.DrainAsync();
        (await h.EntryAsync()).PendingDispatch()!.RunId.Should().Be(9002);

        // The second run outlives postRebaseTimeout: the entry is evicted and that run, the one still going, is cancelled.
        await h.ReleaseAsync(EMergeQueueStep.Timeout);

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("regenerate-timeout");
        entry.EvictionMessage.Should().Contain("regenerate.yml");
        h.Queue.CancelledRuns.Should().Equal([9002L]);
        h.Writer.Publishes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_second_workflow_evicts_regenerate_failed()
    {
        var h = await HarnessAsync(TwoWorkflowsYml);
        h.Writer.PrepareResults.Enqueue(PreparedWith(new GitWriterResolution(ModelHashes, "take-base", true, 0), new GitWriterResolution(LockFile, "take-base", true, 1)));
        h.Queue.DispatchQueue.Enqueue(new(EForgeDispatchOutcome.Dispatched, 9001, null, null));
        h.Queue.DispatchQueue.Enqueue(new(EForgeDispatchOutcome.Dispatched, 9002, null, null));
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "completed", "success", Prepared, "mq/master/pr-7", null);
        h.Queue.Runs[9002] = new ForgeWorkflowRun(9002, "completed", "failure", Prepared, "mq/master/pr-7", null);

        await h.EnqueueAsync();
        await h.DrainAsync();
        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9001);
        await h.DrainAsync();
        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9002);
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Evicted);
        entry.EvictionReason.Should().Be("regenerate-failed");
        entry.EvictionMessage.Should().Contain("regenerate.yml");
        h.Writer.Publishes.Should().BeEmpty();
    }

    [Fact]
    public async Task Prepared_differs_under_another_resolver_version_restarts_instead_of_evicting()
    {
        var h = await HarnessAsync(KeepBothYml);
        h.Writer.PrepareResults.Enqueue(PreparedWith(new GitWriterResolution(Security, "keep-both", false, 1)));
        h.Writer.PublishResults.Enqueue(new GitWriterPublishResult(GitWriterOutcomes.PreparedDiffers, null, null, [], "rebuilt another commit", 5)
        {
            ResolverVersion = "resolution-3; git version 2.51.0",
        });
        h.Writer.PrepareResults.Enqueue(PreparedWith(new GitWriterResolution(Security, "keep-both", false, 1)) with { ResolverVersion = "resolution-3; git version 2.51.0" });
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        h.Writer.Prepares.Should().HaveCount(2, "the entry was prepared again under the new resolver");
        entry.ResolverVersion.Should().Be("resolution-3; git version 2.51.0");
        entry.History.Should().Contain(e => e.Text.Contains("resolver changed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Prepared_differs_under_the_same_resolver_version_still_evicts()
    {
        var h = await HarnessAsync(KeepBothYml);
        h.Writer.PrepareResults.Enqueue(PreparedWith(new GitWriterResolution(Security, "keep-both", false, 1)));
        h.Writer.PublishResults.Enqueue(new GitWriterPublishResult(GitWriterOutcomes.PreparedDiffers, null, null, [], "rebuilt another commit", 5)
        {
            ResolverVersion = "resolution-2; git version 2.50.1",
        });

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).EvictionReason.Should().Be("prepared-differs");
    }

    /// <summary>O36: an entry stored before the multi-workflow dispatch, waiting for its one run in the old fields.</summary>
    [Fact]
    public async Task An_entry_with_the_old_single_dispatch_fields_still_completes()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared, ModelHashes));
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final, ModelHashes));
        h.Queue.Runs[9001] = new ForgeWorkflowRun(9001, "completed", "success", Prepared, "mq/master/pr-7", null);
        h.Queue.Artifacts[9001] = new ForgeArtifactDownload(EForgeArtifactOutcome.Ok, "patch.diff", "diff --git a/x b/x\n"u8.ToArray(), null);
        await h.EnqueueAsync();
        await h.DrainAsync();

        // Rewrite the stored entry as a pre-O36 document: the run only in the legacy fields.
        using (var session = h.Store.OpenAsyncSession())
        {
            var stored = (await session.LoadAsync<MergeQueueEntry>(MergeQueueEntry.DocumentId(RepositoryId, Number)))!;
            stored.Dispatches = [];
            stored.RegenerateWorkflows = [];
            stored.DispatchRunId = 9001;
            stored.DispatchRunUrl = "https://github.com/acme/widget/actions/runs/9001";
            stored.DispatchRunFinished = false;
            await session.SaveChangesAsync();
        }

        await h.SignalAsync(EMergeQueueSignal.RunCompleted, runId: 9001);
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.DispatchRunId.HasValue.Should().BeFalse("the legacy fields moved into Dispatches");
        entry.Dispatches.Should().ContainSingle().Which.Workflow.Should().Be("regenerate.yml");
        h.Writer.Publishes.Should().ContainSingle().Which.Patch.Should().Be(Convert.ToBase64String("diff --git a/x b/x\n"u8.ToArray()));
    }
}
