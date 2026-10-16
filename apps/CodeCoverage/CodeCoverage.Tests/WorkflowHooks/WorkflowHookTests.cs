using System.Text;
using CodeCoverage.Forge;
using CodeCoverage.Tests.MergeQueue;
using CodeCoverage.WorkflowHooks;
using Xunit;
using static CodeCoverage.Tests.WorkflowHooks.WorkflowHookHarness;

namespace CodeCoverage.Tests.WorkflowHooks;

/// <summary>
/// The workflow hook (dependency-updates PRD §13.8 N1, §13.11.1), through a minimal saga with its state in a real RavenDB
/// and the forge scripted: dispatch and correlation, the wait (webhook, poll, timeout), the outcome check, artifacts,
/// provenance, generation gating and writer tokens. The merge queue's own suites hold the same rules for its saga.
/// </summary>
public class WorkflowHookTests : CoverageRavenTest
{
    private async Task<WorkflowHookHarness> HarnessAsync(Action<ProbeDocument>? configure = null)
    {
        var harness = new WorkflowHookHarness(GetDocumentStore());
        await harness.SeedAsync(configure);
        return harness;
    }

    // ── Dispatch and correlation ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_dispatch_the_forge_names_waits_with_a_poll_and_a_timeout_of_the_requested_length()
    {
        var h = await HarnessAsync(p => p.TimeoutMinutes = 17);

        await h.StartAsync();
        await h.DrainAsync();

        var probe = await h.ProbeAsync();
        probe.State.Should().Be(EProbeState.Waiting);
        probe.Runs.Select(r => r.RunId).Should().Equal([9001L]);
        probe.Runs[0].RunUrl.Should().Be("https://github.com/acme/widget/actions/runs/9001");
        h.Queue.Dispatches.Should().ContainSingle();
        h.Queue.Dispatches[0].Workflow.Should().Be("lookup.yml");
        h.Queue.Dispatches[0].Branch.Should().Be("mintplayer/dependencies-work");
        h.Queue.Dispatches[0].Inputs["expected_sha"].Should().Be(Expected);
        h.Delayed.Select(d => (d.Step.Step, d.Delay)).Should().Equal([(EProbeStep.PollRun, WorkflowHook.RunPollInterval), (EProbeStep.Timeout, TimeSpan.FromMinutes(17))]);
        h.Delayed.Should().OnlyContain(d => d.Step.Generation == probe.Generation, "the timers are armed after the saga's own transition");
    }

    [Fact]
    public async Task A_204_without_a_run_id_is_dispatch_uncorrelated_and_nothing_waits()
    {
        var h = await HarnessAsync();
        h.Queue.NextDispatch = new(EForgeDispatchOutcome.Dispatched, null, null, null);

        await h.StartAsync();
        await h.DrainAsync();

        var probe = await h.ProbeAsync();
        probe.State.Should().Be(EProbeState.Ended);
        probe.Outcome.Should().Be("dispatch-uncorrelated");
        probe.Runs.Should().BeEmpty();
        h.Delayed.Should().BeEmpty("no poll and no timeout for a run nobody can name");
    }

    [Fact]
    public async Task A_refused_dispatch_carries_the_forges_reason()
    {
        var h = await HarnessAsync();
        h.Queue.NextDispatch = new(EForgeDispatchOutcome.Refused, null, null, "Workflow does not have 'workflow_dispatch' trigger");

        await h.StartAsync();
        await h.DrainAsync();

        var probe = await h.ProbeAsync();
        probe.Outcome.Should().Be("dispatch-refused");
        probe.Detail.Should().Be("Workflow does not have 'workflow_dispatch' trigger");
        h.Delayed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stray_run_left_by_a_lost_dispatch_answer_is_cancelled_before_the_next_dispatch()
    {
        var h = await HarnessAsync();
        h.Queue.DispatchThrows = new ForgeRequestException("502 Bad Gateway", 502);
        h.Queue.Runs[9001] = InProgress(9001);

        Func<Task> lost = h.StartAsync;
        await lost.Should().ThrowAsync<ForgeRequestException>();
        h.Queue.NextDispatch = new(EForgeDispatchOutcome.Dispatched, 9002, null, null);
        await h.StartAsync();
        await h.DrainAsync();

        var probe = await h.ProbeAsync();
        h.Queue.CancelledRuns.Should().Equal([9001L]);
        probe.History.Should().Contain(e => e.Contains("cancelled the stray probe run 9001", StringComparison.Ordinal));
        probe.Runs.Select(r => r.RunId).Should().Equal([9002L]);
    }

    // ── Waiting: webhook, poll, timeout ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_a_webhook_the_poll_finds_the_run_and_a_running_run_is_polled_again()
    {
        var h = await HarnessAsync();
        await h.StartAsync();
        h.Queue.Runs[9001] = InProgress(9001);

        await h.ReleaseAsync(EProbeStep.PollRun);
        (await h.ProbeAsync()).State.Should().Be(EProbeState.Waiting);
        h.Delayed.Count(d => d.Step.Step == EProbeStep.PollRun).Should().Be(1, "a running run is polled again");

        h.Queue.Runs[9001] = Completed(9001);
        h.Queue.Artifacts[9001] = Ok(Encoding.UTF8.GetBytes("{}"));
        await h.ReleaseAsync(EProbeStep.PollRun);

        var probe = await h.ProbeAsync();
        probe.State.Should().Be(EProbeState.Succeeded);
        probe.Runs[0].Finished.Should().BeTrue();
        probe.Runs[0].Succeeded.Should().BeTrue();
        probe.Artifact.Should().Equal(Encoding.UTF8.GetBytes("{}"));
    }

    [Fact]
    public async Task The_webhook_does_not_start_a_second_poll_chain()
    {
        var h = await HarnessAsync();
        await h.StartAsync();
        await h.DrainAsync();
        h.Queue.Runs[9001] = InProgress(9001);

        await h.RunCompletedAsync(9001);
        await h.DrainAsync();

        h.Delayed.Count(d => d.Step.Step == EProbeStep.PollRun).Should().Be(1);
    }

    [Fact]
    public async Task The_timeout_ends_the_wait_and_cancels_the_run()
    {
        var h = await HarnessAsync();
        await h.StartAsync();
        h.Queue.Runs[9001] = InProgress(9001);

        await h.ReleaseAsync(EProbeStep.Timeout);

        var probe = await h.ProbeAsync();
        probe.Outcome.Should().Be("timeout");
        probe.Runs[0].Finished.Should().BeTrue();
        probe.Runs[0].Succeeded.Should().BeFalse();
        h.Queue.CancelledRuns.Should().Equal([9001L]);
        probe.History.Should().Contain(e => e.EndsWith("cancelled the abandoned probe run 9001", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_step_for_a_generation_the_saga_has_left_is_dropped()
    {
        var h = await HarnessAsync();
        await h.StartAsync();
        await h.DrainAsync();
        h.Queue.Runs[9001] = Completed(9001);
        h.Queue.Artifacts[9001] = Ok([1, 2, 3]);

        // The webhook wins the race; the poll and the timeout armed for the wait are now stale.
        await h.RunCompletedAsync(9001);
        await h.ReleaseAsync(EProbeStep.Timeout);
        await h.ReleaseAsync(EProbeStep.PollRun);

        var probe = await h.ProbeAsync();
        probe.State.Should().Be(EProbeState.Succeeded);
        probe.Outcome.Should().BeNull("the stale timeout did not end a saga that had already succeeded");
        h.Queue.CancelledRuns.Should().BeEmpty();
        h.Queue.ArtifactReads.Should().ContainSingle("the stale poll did not look at the run again");
        probe.History.Count(e => e.Contains("dropped a stale", StringComparison.Ordinal)).Should().Be(2);
    }

    // ── The outcome check (D14) ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_green_run_of_another_commit_is_not_a_success()
    {
        var h = await HarnessAsync();
        await h.StartAsync();
        await h.DrainAsync();
        h.Queue.Runs[9001] = Completed(9001, headSha: Other);
        h.Queue.Artifacts[9001] = Ok([1]);

        await h.RunCompletedAsync(9001);
        await h.DrainAsync();

        var probe = await h.ProbeAsync();
        probe.Outcome.Should().Be("run-wrong-commit");
        probe.Detail.Should().Be(Other);
        probe.Runs[0].Finished.Should().BeTrue();
        probe.Runs[0].Succeeded.Should().BeFalse();
        h.Queue.ArtifactReads.Should().BeEmpty("nothing a run of another commit produced is read");
        h.Queue.CancelledRuns.Should().BeEmpty("a finished run has nothing to cancel");
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancelled")]
    [InlineData("timed_out")]
    public async Task A_run_that_does_not_conclude_success_fails(string conclusion)
    {
        var h = await HarnessAsync();
        await h.StartAsync();
        await h.DrainAsync();
        h.Queue.Runs[9001] = Completed(9001, conclusion);

        await h.RunCompletedAsync(9001);

        var probe = await h.ProbeAsync();
        probe.Outcome.Should().Be("run-failed");
        probe.Detail.Should().Be(conclusion);
        probe.Runs[0].Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task A_run_that_vanished_is_missing()
    {
        var h = await HarnessAsync();
        await h.StartAsync();

        await h.ReleaseAsync(EProbeStep.PollRun);

        var probe = await h.ProbeAsync();
        probe.Outcome.Should().Be("run-missing");
        h.Queue.CancelledRuns.Should().BeEmpty();
    }

    // ── Artifacts ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_named_artifact_is_read_under_the_callers_cap()
    {
        var h = await HarnessAsync(p => { p.ArtifactName = "result.json"; p.MaxArtifactBytes = 1024; });
        await h.StartAsync();
        await h.DrainAsync();
        h.Queue.Runs[9001] = Completed(9001);
        h.Queue.Artifacts[9001] = Ok(Encoding.UTF8.GetBytes("{\"applied\":[]}"));

        await h.RunCompletedAsync(9001);

        h.Queue.ArtifactReads.Should().Equal([(9001L, "result.json", 1024L)]);
        (await h.ProbeAsync()).State.Should().Be(EProbeState.Succeeded);
    }

    [Fact]
    public async Task No_caller_raises_the_cap_past_the_hooks_ceiling()
    {
        var h = await HarnessAsync(p => p.MaxArtifactBytes = long.MaxValue);
        await h.StartAsync();
        await h.DrainAsync();
        h.Queue.Runs[9001] = Completed(9001);
        h.Queue.Artifacts[9001] = Ok([1]);

        await h.RunCompletedAsync(9001);

        h.Queue.ArtifactReads.Should().Equal([(9001L, "lookup.json", WorkflowHook.MaxArtifactBytes)]);
    }

    [Fact]
    public async Task An_artifact_over_the_cap_is_too_large_even_when_the_forge_handed_it_over()
    {
        var h = await HarnessAsync(p => p.MaxArtifactBytes = 4);
        await h.StartAsync();
        await h.DrainAsync();
        h.Queue.Runs[9001] = Completed(9001);
        h.Queue.Artifacts[9001] = Ok([1, 2, 3, 4, 5]);

        await h.RunCompletedAsync(9001);

        var probe = await h.ProbeAsync();
        probe.Outcome.Should().Be("artifact-toolarge");
        probe.Artifact.Should().BeNull();
    }

    [Theory]
    [InlineData(EForgeArtifactOutcome.TooLarge, "artifact-toolarge")]
    [InlineData(EForgeArtifactOutcome.Expired, "artifact-expired")]
    [InlineData(EForgeArtifactOutcome.NotFound, "artifact-notfound")]
    [InlineData(EForgeArtifactOutcome.Invalid, "artifact-invalid")]
    public async Task An_unusable_artifact_ends_the_saga_with_its_reason(EForgeArtifactOutcome outcome, string expected)
    {
        var h = await HarnessAsync();
        await h.StartAsync();
        await h.DrainAsync();
        h.Queue.Runs[9001] = Completed(9001);
        h.Queue.Artifacts[9001] = new ForgeArtifactDownload(outcome, null, null, "the forge's words");

        await h.RunCompletedAsync(9001);

        var probe = await h.ProbeAsync();
        probe.Outcome.Should().Be(expected);
        probe.Detail.Should().Be("the forge's words");
        probe.Runs[0].Succeeded.Should().BeTrue("the run itself succeeded; its artifact is what failed");
    }

    // ── Provenance (D12) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_workflow_whose_blob_differs_from_the_default_branchs_is_never_dispatched()
    {
        var h = await HarnessAsync();
        h.Queue.Blobs[$"{Expected}:{WorkflowPath}"] = "e7e7";

        await h.StartAsync();
        await h.DrainAsync();

        var probe = await h.ProbeAsync();
        probe.Outcome.Should().Be("workflow-differs");
        probe.Detail.Should().Be(WorkflowPath);
        h.Queue.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task A_workflow_the_default_branch_lacks_is_missing()
    {
        var h = await HarnessAsync();
        h.Queue.Blobs.Remove($"master:{WorkflowPath}");

        await h.StartAsync();

        var probe = await h.ProbeAsync();
        probe.Outcome.Should().Be("workflow-missing");
        h.Queue.Dispatches.Should().BeEmpty();
    }

    // ── Writer tokens (T9) ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_writer_job_gets_its_own_token_which_is_revoked_even_when_the_job_throws()
    {
        var h = await HarnessAsync();
        var saga = new DirectSaga(h);

        var answer = await h.Hook.RunWriterJobAsync(saga, null, pushesWorkflowChanges: true, (access, credential) => Task.FromResult(credential.Password));
        Func<Task> throwing = () => h.Hook.RunWriterJobAsync<string>(saga, null, false, (_, _) => throw new HttpRequestException("writer down"));
        await throwing.Should().ThrowAsync<HttpRequestException>();

        answer.Ran.Should().BeTrue();
        answer.Result.Should().Be("job-token-1");
        h.Queue.WriterAccesses.Select(a => (a.Workflows, a.Token)).Should().Equal([(true, "job-token-1"), (false, "job-token-2")]);
        h.Queue.RevokedTokens.Should().Equal(["job-token-1", "job-token-2"]);
    }

    [Fact]
    public async Task A_writer_job_the_forge_gives_no_token_never_runs()
    {
        var h = await HarnessAsync();
        h.Queue.WriterAccess = _ => ForgePushAccess.Refused("The App is not installed on the fork.");
        var ran = false;

        var answer = await new WorkflowHook(Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowHook>.Instance)
            .RunWriterJobAsync(new DirectSaga(h), null, false, (_, _) => { ran = true; return Task.FromResult(0); });

        answer.Ran.Should().BeFalse();
        answer.Refusal.Should().Be("The App is not installed on the fork.");
        ran.Should().BeFalse();
        h.Queue.RevokedTokens.Should().BeEmpty();
    }

    /// <summary>The hook's caller side without a document, for the calls that change no saga state.</summary>
    private sealed class DirectSaga(WorkflowHookHarness harness) : IWorkflowHookSaga
    {
        public Entities.Repository Repository { get; } = new() { GitHubId = RepoGitHubId, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme", DefaultBranch = "master" };

        public IForgeMergeQueue Forge => harness.Queue;

        public void Note(string text) { }

        public Task ScheduleAsync(EWorkflowHookStep step, TimeSpan? delay, long? runId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
