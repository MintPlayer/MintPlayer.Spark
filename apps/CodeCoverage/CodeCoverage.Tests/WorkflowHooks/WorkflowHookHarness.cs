using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Tests.CommitGraph;
using CodeCoverage.Tests.MergeQueue;
using CodeCoverage.WorkflowHooks;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Tests.WorkflowHooks;

/// <summary>
/// A minimal saga on the workflow hook, the shape the merge queue has and the dependency updater will have: its state in
/// one RavenDB document, its steps through the outbox stamped with its <see cref="ProbeDocument.Generation"/>, a step for
/// a generation it has left dropped. It dispatches one workflow, waits for it, and reads one named artifact.
/// </summary>
public sealed class ProbeDocument
{
    public string? Id { get; set; }
    public long Generation { get; set; }
    public EProbeState State { get; set; }

    /// <summary>Why it ended, as the probe names the hook's outcomes.</summary>
    public string? Outcome { get; set; }

    public string? Detail { get; set; }
    public string Workflow { get; set; } = "lookup.yml";
    public string Ref { get; set; } = "mintplayer/dependencies-work";
    public string ExpectedSha { get; set; } = string.Empty;
    public int TimeoutMinutes { get; set; } = 30;
    public string ArtifactName { get; set; } = "lookup.json";
    public long MaxArtifactBytes { get; set; } = WorkflowHook.DefaultMaxArtifactBytes;
    public List<WorkflowHookRun> Runs { get; set; } = [];
    public byte[]? Artifact { get; set; }
    public List<string> History { get; set; } = [];
}

public enum EProbeState
{
    Idle,
    Waiting,
    Succeeded,
    Ended,
}

/// <summary>The probe's one message type: its own steps, and the <c>workflow_run</c> webhook as a signal.</summary>
public sealed class ProbeStep
{
    public string DocumentId { get; set; } = string.Empty;
    public long Generation { get; set; }
    public EProbeStep Step { get; set; }
    public long? RunId { get; set; }
}

public enum EProbeStep
{
    Start,
    PollRun,
    Timeout,
    CancelRun,
    RunCompleted,
}

/// <summary>Drives the probe the way the messaging host would (see <see cref="MergeQueueHarness"/>, which this generalizes).</summary>
internal sealed class WorkflowHookHarness(IDocumentStore store)
{
    public const string DocumentId = "probes/1";
    public const string Expected = "4444444444444444444444444444444444444444";
    public const string Other = "9999999999999999999999999999999999999999";
    public const long RepoGitHubId = 910041;
    public const string WorkflowPath = ".github/workflows/lookup.yml";

    public static readonly string RepositoryId = Entities.Repository.DocumentId(EForgeProvider.GitHub, RepoGitHubId);

    public FakeForgeMergeQueue Queue { get; } = new();
    public FakeOutbox Outbox { get; } = new();
    public IWorkflowHook Hook { get; } = new WorkflowHook(NullLogger<WorkflowHook>.Instance);
    public MutableTime Time { get; } = new(new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero));
    public List<(ProbeStep Step, TimeSpan Delay)> Delayed { get; } = [];
    public List<ProbeStep> Handled { get; } = [];

    public async Task SeedAsync(Action<ProbeDocument>? configure = null)
    {
        Queue.Blobs[$"master:{WorkflowPath}"] = "b10b";
        Queue.Blobs[$"{Expected}:{WorkflowPath}"] = "b10b";
        Queue.NextDispatch = new(EForgeDispatchOutcome.Dispatched, 9001, "https://github.com/acme/widget/actions/runs/9001", null);

        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Entities.Repository
        {
            GitHubId = RepoGitHubId, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme", DefaultBranch = "master", Provider = EForgeProvider.GitHub,
        }, RepositoryId);
        var probe = new ProbeDocument { Id = DocumentId, ExpectedSha = Expected };
        configure?.Invoke(probe);
        await session.StoreAsync(probe, DocumentId);
        await session.SaveChangesAsync();
    }

    /// <summary>A completed run of the probe's ref.</summary>
    public static ForgeWorkflowRun Completed(long id, string conclusion = "success", string headSha = Expected)
        => new(id, "completed", conclusion, headSha, "mintplayer/dependencies-work", $"https://github.com/acme/widget/actions/runs/{id}");

    public static ForgeWorkflowRun InProgress(long id)
        => new(id, "in_progress", null, Expected, "mintplayer/dependencies-work", $"https://github.com/acme/widget/actions/runs/{id}");

    public static ForgeArtifactDownload Ok(byte[] content) => new(EForgeArtifactOutcome.Ok, "lookup.json", content, null);

    public Task StartAsync() => StepAsync(new ProbeStep { DocumentId = DocumentId, Step = EProbeStep.Start, Generation = 0 });

    public Task RunCompletedAsync(long runId) => StepAsync(new ProbeStep { DocumentId = DocumentId, Step = EProbeStep.RunCompleted, RunId = runId });

    public async Task StepAsync(ProbeStep step)
    {
        Handled.Add(step);
        using var session = store.OpenAsyncSession();
        var probe = (await session.LoadAsync<ProbeDocument>(step.DocumentId))!;
        var repository = (await session.LoadAsync<Entities.Repository>(RepositoryId))!;
        await new ProbeSaga(this, session, probe, repository).HandleAsync(step);
        await session.SaveChangesAsync();
    }

    /// <summary>Delivers every immediate step, in order; delayed ones are set aside with their delay.</summary>
    public async Task DrainAsync()
    {
        for (var i = 0; i < 100; i++)
        {
            if (!Outbox.Messages.TryDequeue(out var queued)) return;
            var step = (ProbeStep)queued.Message;
            if (queued.Options?.Delay is { } delay)
            {
                Delayed.Add((step, delay));
                continue;
            }
            await StepAsync(step);
        }
        throw new InvalidOperationException("The probe did not settle within 100 steps.");
    }

    /// <summary>Delivers the most recently delayed step of <paramref name="kind"/>, then drains.</summary>
    public async Task ReleaseAsync(EProbeStep kind)
    {
        await DrainAsync();
        var delayed = Delayed.LastOrDefault(d => d.Step.Step == kind);
        if (delayed.Step is null) throw new InvalidOperationException($"No delayed {kind} step.");
        Delayed.Remove(delayed);
        Time.Now += delayed.Delay;
        await StepAsync(delayed.Step);
        await DrainAsync();
    }

    public async Task<ProbeDocument> ProbeAsync()
    {
        using var session = store.OpenAsyncSession();
        return (await session.LoadAsync<ProbeDocument>(DocumentId))!;
    }

    /// <summary>The probe's saga: one per handled message, over the document it loaded.</summary>
    private sealed class ProbeSaga(WorkflowHookHarness harness, IAsyncDocumentSession session, ProbeDocument probe, Entities.Repository repository) : IWorkflowHookSaga
    {
        public Entities.Repository Repository => repository;

        public IForgeMergeQueue Forge => harness.Queue;

        public void Note(string text) => probe.History.Add($"{harness.Time.GetUtcNow():O} {text}");

        public Task ScheduleAsync(EWorkflowHookStep step, TimeSpan? delay, long? runId, CancellationToken cancellationToken)
            => harness.Outbox.EnqueueAsync(session, new ProbeStep
            {
                DocumentId = probe.Id!,
                Generation = probe.Generation,
                Step = step switch
                {
                    EWorkflowHookStep.PollRun => EProbeStep.PollRun,
                    EWorkflowHookStep.Timeout => EProbeStep.Timeout,
                    _ => EProbeStep.CancelRun,
                },
                RunId = runId,
            }, delay is { } wait ? new BroadcastOptions { Delay = wait } : null, cancellationToken);

        private IWorkflowHook Hook => harness.Hook;

        public async Task HandleAsync(ProbeStep step)
        {
            switch (step.Step)
            {
                case EProbeStep.CancelRun when step.RunId is { } runId:
                    // Not gated: the probe has moved on by design.
                    await Hook.CancelAbandonedRunAsync(this, "probe", runId);
                    return;
                case EProbeStep.RunCompleted:
                    // A signal: matched on the run it names, not on the generation.
                    if (probe.State == EProbeState.Waiting && probe.Runs.LastOrDefault() is { } current && current.RunId == step.RunId)
                        await ObserveAsync(current, fromPoll: false);
                    return;
            }

            // A step for a generation the probe has left is dropped.
            if (step.Generation != probe.Generation)
            {
                Note($"dropped a stale {step.Step} (generation {step.Generation}, now {probe.Generation})");
                return;
            }

            switch (step.Step)
            {
                case EProbeStep.Start when probe.State == EProbeState.Idle:
                    await StartAsync();
                    break;
                case EProbeStep.PollRun when probe.State == EProbeState.Waiting && probe.Runs.LastOrDefault() is { } polled:
                    await ObserveAsync(polled, fromPoll: true);
                    break;
                case EProbeStep.Timeout when probe.State == EProbeState.Waiting:
                    await EndAsync("timeout", $"the run did not finish within {probe.TimeoutMinutes} minutes");
                    break;
            }
        }

        private async Task StartAsync()
        {
            if (await Hook.VerifyProvenanceAsync(this, "master", probe.ExpectedSha, [probe.Workflow]) is { } failure)
            {
                await EndAsync(failure.Kind == EWorkflowHookProvenance.Missing ? "workflow-missing" : "workflow-differs", failure.Path);
                return;
            }

            var request = new WorkflowHookRequest(probe.Workflow, probe.Ref, new Dictionary<string, string> { ["expected_sha"] = probe.ExpectedSha },
                TimeSpan.FromMinutes(probe.TimeoutMinutes), "probe");
            var dispatch = await Hook.DispatchAsync(this, request, dispatched =>
            {
                probe.Runs.Add(new WorkflowHookRun { Workflow = dispatched.Workflow, RunId = dispatched.RunId, RunUrl = dispatched.RunUrl });
                Transition(EProbeState.Waiting);
                return Task.CompletedTask;
            });
            switch (dispatch.Outcome)
            {
                case EWorkflowHookDispatch.Refused:
                    await EndAsync("dispatch-refused", dispatch.Message);
                    break;
                case EWorkflowHookDispatch.Uncorrelated:
                    await EndAsync("dispatch-uncorrelated", null);
                    break;
            }
        }

        private async Task ObserveAsync(WorkflowHookRun run, bool fromPoll)
        {
            var observation = await Hook.ObserveAsync(this, run, probe.ExpectedSha, fromPoll);
            switch (observation.Verdict)
            {
                case EWorkflowHookRunVerdict.Missing:
                    await EndAsync("run-missing", null);
                    return;
                case EWorkflowHookRunVerdict.WrongCommit:
                    await EndAsync("run-wrong-commit", observation.Run!.HeadSha);
                    return;
                case EWorkflowHookRunVerdict.Failed:
                    await EndAsync("run-failed", observation.Run!.Conclusion);
                    return;
                case EWorkflowHookRunVerdict.Succeeded:
                    var artifact = await Hook.DownloadArtifactAsync(this, run.RunId!.Value, probe.ArtifactName, probe.MaxArtifactBytes);
                    if (artifact.Outcome != EForgeArtifactOutcome.Ok)
                    {
                        await EndAsync($"artifact-{artifact.Outcome.ToString().ToLowerInvariant()}", artifact.Detail);
                        return;
                    }
                    probe.Artifact = artifact.Content;
                    Transition(EProbeState.Succeeded);
                    return;
            }
        }

        private async Task EndAsync(string outcome, string? detail)
        {
            probe.Outcome = outcome;
            probe.Detail = detail;
            await Hook.AbandonAsync(this, probe.Runs);
            Transition(EProbeState.Ended);
        }

        private void Transition(EProbeState state)
        {
            probe.State = state;
            probe.Generation++;
            Note($"now {state}");
        }
    }
}
