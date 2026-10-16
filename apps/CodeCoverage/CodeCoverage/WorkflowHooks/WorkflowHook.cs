using CodeCoverage.CommitGraph;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.WorkflowHooks;

/// <summary>
/// The workflow hook (dependency-updates PRD §13.8 N1, S15): running one of a repository's own GitHub Actions workflows
/// on the server's behalf, and trusting its result only as far as it can be checked. The merge queue's regenerate step
/// runs on it, and so will the dependency updater's lookup, apply and migrate runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>The caller's saga keeps the state; the hook keeps the rules.</b> A saga stores its runs (<see cref="WorkflowHookRun"/>)
/// in its own document and hands the hook a <see cref="IWorkflowHookSaga"/>: the repository, a way to note what happened,
/// and a way to queue the saga's own step messages, stamped with its own generation (so a step for a state the saga has
/// left is dropped by the saga, exactly as before). The hook never saves anything and never ends a saga: every outcome
/// it reports is typed, and what to say about it is the caller's.
/// </para>
/// <para>
/// <b>What it hides:</b> D12's workflow provenance (the dispatched file must be the default branch's, byte for byte),
/// D65's fork precondition, D53's stray and abandoned runs, D14's correlation (a dispatch whose run id is unknown is not
/// trusted) and its outcome check (the run built the expected commit, and succeeded), the poll that stands in for a lost
/// <c>workflow_run</c> webhook, the timeout per run, the capped artifact read, and T9's one token per git-writer job.
/// </para>
/// <para>
/// <b>Errors</b> are the forge's (<see cref="IForgeMergeQueue"/>): a refused permission throws
/// <see cref="ForgeAccessDeniedException"/>, anything that may pass throws (<see cref="IsTransient"/>), and the saga
/// decides what to do with either.
/// </para>
/// </remarks>
public interface IWorkflowHook
{
    /// <summary>
    /// D65 (d): before a fork's commit is pushed to a branch of the base repository so a workflow can run on it, every
    /// workflow that will run must exist on the default branch, and nothing there may run that commit with the
    /// repository's secrets (<see cref="MergeQueueForkGuard"/>).
    /// </summary>
    Task<WorkflowHookForkVerdict> CheckForkAsync(IWorkflowHookSaga saga, WorkflowHookForkCheck check, CancellationToken cancellationToken = default);

    /// <summary>
    /// D12: a dispatched run executes the workflow file of the ref it runs on, so each of <paramref name="workflows"/> at
    /// <paramref name="commitSha"/> must be byte-for-byte the one on <paramref name="defaultBranch"/>. Null: every one is.
    /// Read in order; the first that is not is the answer.
    /// </summary>
    Task<WorkflowHookProvenanceFailure?> VerifyProvenanceAsync(IWorkflowHookSaga saga, string defaultBranch, string commitSha, IReadOnlyList<string> workflows, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatches one run: cancels a stray run of the workflow on the ref first (D53), dispatches, and accepts only a run
    /// the forge named (D14). On <see cref="EWorkflowHookDispatch.Dispatched"/> it calls <paramref name="onDispatched"/>,
    /// where the saga records the run and moves on, then queues the saga's <see cref="EWorkflowHookStep.PollRun"/> and its
    /// <see cref="EWorkflowHookStep.Timeout"/> after <see cref="WorkflowHookRequest.Timeout"/>, in that order, with whatever
    /// generation the saga has then.
    /// </summary>
    Task<WorkflowHookDispatch> DispatchAsync(IWorkflowHookSaga saga, WorkflowHookRequest request, Func<WorkflowHookDispatch, Task> onDispatched, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks at <paramref name="run"/>, on its <c>workflow_run</c> webhook or on the saga's poll step: a run still going is
    /// polled again (only from a poll, so a webhook never doubles the poll chain); a finished run is marked
    /// <see cref="WorkflowHookRun.Finished"/>, and <see cref="WorkflowHookRun.Succeeded"/> only when it built
    /// <paramref name="expectedSha"/> and concluded <c>success</c> (D14).
    /// </summary>
    Task<WorkflowHookObservation> ObserveAsync(IWorkflowHookSaga saga, WorkflowHookRun run, string expectedSha, bool fromPoll, CancellationToken cancellationToken = default);

    /// <summary>
    /// The saga stops waiting for <paramref name="runs"/> for any reason but their own completion (a moved base, an
    /// eviction, a timeout): each one not finished yet is marked finished and its cancellation queued as the saga's
    /// <see cref="EWorkflowHookStep.CancelRun"/>, so it is only cancelled when the state change that abandons it commits.
    /// </summary>
    Task AbandonAsync(IWorkflowHookSaga saga, IEnumerable<WorkflowHookRun> runs, CancellationToken cancellationToken = default);

    /// <summary>
    /// The saga's <see cref="EWorkflowHookStep.CancelRun"/> step: cancels an abandoned run. A forge failure that may pass
    /// throws, so the messaging runtime retries the step; a refused permission is noted and dropped. <paramref name="purpose"/>
    /// names the run in the note ("regenerate", "lookup").
    /// </summary>
    Task CancelAbandonedRunAsync(IWorkflowHookSaga saga, string purpose, long runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The single file of the run's artifact <paramref name="artifactName"/> (a patch, <c>lookup.json</c>,
    /// <c>result.json</c>), refused beyond <paramref name="maxBytes"/>, compressed or not, and when the archive holds
    /// anything but one file. <paramref name="maxBytes"/> is held to <see cref="WorkflowHook.MaxArtifactBytes"/>.
    /// </summary>
    Task<ForgeArtifactDownload> DownloadArtifactAsync(IWorkflowHookSaga saga, long runId, string artifactName, long maxBytes = WorkflowHook.DefaultMaxArtifactBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// One git-writer job with a token of its own (T9, §0 D59): minted for the repository (and the pull request's fork,
    /// O3), with the workflow permission only when <paramref name="pushesWorkflowChanges"/>, and revoked as soon as the
    /// job answers or throws. When the forge gives no token, the job does not run and the answer says why.
    /// </summary>
    Task<WorkflowHookWriterJob<T>> RunWriterJobAsync<T>(IWorkflowHookSaga saga, ForgePullRequest? pullRequest, bool pushesWorkflowChanges, Func<ForgeWriterAccess, SidecarCredential, Task<T>> job, CancellationToken cancellationToken = default);
}

/// <summary>
/// The caller's side of the hook: its repository, its history and its own step messages. A saga makes one per handled
/// message, over the document it loaded.
/// </summary>
public interface IWorkflowHookSaga
{
    Repository Repository { get; }

    /// <summary>The repository's forge, as the saga speaks to it.</summary>
    IForgeMergeQueue Forge { get; }

    /// <summary>Adds a line to the saga's own history.</summary>
    void Note(string text);

    /// <summary>
    /// Queues the saga's own step message for <paramref name="step"/>, through its outbox with whatever it saves, stamped
    /// with its current generation; <paramref name="runId"/> only for <see cref="EWorkflowHookStep.CancelRun"/>.
    /// </summary>
    Task ScheduleAsync(EWorkflowHookStep step, TimeSpan? delay, long? runId, CancellationToken cancellationToken);
}

/// <summary>The steps the hook asks the saga to queue for it.</summary>
public enum EWorkflowHookStep
{
    /// <summary>Look at the run again (<see cref="IWorkflowHook.ObserveAsync"/> with <c>fromPoll</c>): the webhook may be lost.</summary>
    PollRun,

    /// <summary>The run's time is up; the saga ends the wait and abandons the run.</summary>
    Timeout,

    /// <summary>Cancel an abandoned run (<see cref="IWorkflowHook.CancelAbandonedRunAsync"/>); not gated on the generation.</summary>
    CancelRun,
}

/// <summary>One dispatch.</summary>
/// <param name="Workflow">The file name under <c>.github/workflows</c>; it must be registered on the default branch.</param>
/// <param name="Ref">The branch the run runs on (the queue branch, the dependency updater's work ref).</param>
/// <param name="Inputs">The <c>workflow_dispatch</c> inputs (<c>expected_sha</c> and the caller's own).</param>
/// <param name="Timeout">How long the saga waits for this run before its <see cref="EWorkflowHookStep.Timeout"/>.</param>
/// <param name="Purpose">What the run is for, in the saga's history ("regenerate", "lookup", "apply").</param>
public sealed record WorkflowHookRequest(string Workflow, string Ref, IReadOnlyDictionary<string, string> Inputs, TimeSpan Timeout, string Purpose);

/// <summary>What a dispatch did.</summary>
/// <param name="Message">The forge's own reason, for <see cref="EWorkflowHookDispatch.Refused"/>.</param>
public sealed record WorkflowHookDispatch(EWorkflowHookDispatch Outcome, string Workflow, long? RunId, string? RunUrl, EForgeDispatchOutcome ForgeOutcome, string? Message);

public enum EWorkflowHookDispatch
{
    /// <summary>The run started, and the forge said which one it is.</summary>
    Dispatched,

    /// <summary>The forge refused the dispatch (<see cref="WorkflowHookDispatch.ForgeOutcome"/>, <see cref="WorkflowHookDispatch.Message"/>).</summary>
    Refused,

    /// <summary>The forge started a run without saying which (a 204): its result could not be trusted, so nothing waits for it.</summary>
    Uncorrelated,
}

/// <summary>What a look at a run found.</summary>
/// <param name="Run">The forge's run, when it still exists.</param>
public sealed record WorkflowHookObservation(EWorkflowHookRunVerdict Verdict, ForgeWorkflowRun? Run);

public enum EWorkflowHookRunVerdict
{
    /// <summary>Still running (or nothing to look at: the run had already finished).</summary>
    Running,

    /// <summary>The run no longer exists.</summary>
    Missing,

    /// <summary>The run built another commit than the expected one (D14); it proves nothing about ours.</summary>
    WrongCommit,

    /// <summary>The run built the expected commit and concluded anything but <c>success</c>.</summary>
    Failed,

    /// <summary>The run built the expected commit and succeeded.</summary>
    Succeeded,
}

/// <summary>D65 (d)'s question: the workflows that will run on <paramref name="DispatchBranch"/> once a fork's commit is pushed there.</summary>
/// <param name="PushedRef">Where the pushed commit's own workflows are read (the base commit: the fork's change to them never gets there).</param>
/// <param name="PushedBranch">The branch <paramref name="PushedRef"/> is on, to name it.</param>
/// <param name="DefaultBranch">The default branch, whose workflows run on <c>workflow_dispatch</c> and <c>workflow_run</c>.</param>
public sealed record WorkflowHookForkCheck(string PushedRef, string PushedBranch, string DefaultBranch, IReadOnlyList<string> Workflows, string DispatchBranch);

/// <summary>D65 (d)'s answer.</summary>
/// <param name="MissingWorkflow">The first workflow (as <c>.github/workflows/…</c>) the default branch does not have; then no finding was looked for.</param>
/// <param name="Findings">Every reason, each once, not to push the fork's commit; empty: it may be pushed.</param>
public sealed record WorkflowHookForkVerdict(string? MissingWorkflow, IReadOnlyList<ForkGuardFinding> Findings)
{
    public bool Holds => MissingWorkflow is null && Findings.Count == 0;
}

/// <summary>Why D12's provenance check failed for <paramref name="Path"/>.</summary>
public sealed record WorkflowHookProvenanceFailure(EWorkflowHookProvenance Kind, string Path);

public enum EWorkflowHookProvenance
{
    /// <summary>The default branch has no such workflow, so it cannot be dispatched.</summary>
    Missing,

    /// <summary>The ref's copy is not the default branch's.</summary>
    Differs,
}

/// <summary>A git-writer job's answer, or why it never ran.</summary>
/// <param name="Refusal">The forge's reason for giving no token, when <paramref name="Ran"/> is false; may be null.</param>
public sealed record WorkflowHookWriterJob<T>(bool Ran, T? Result, string? Refusal);

/// <inheritdoc cref="IWorkflowHook"/>
[Register(typeof(IWorkflowHook), ServiceLifetime.Scoped)]
public partial class WorkflowHook : IWorkflowHook
{
    [Inject] private readonly ILogger<WorkflowHook> logger;

    /// <summary>How often a run is looked at when its webhook does not come.</summary>
    public static readonly TimeSpan RunPollInterval = TimeSpan.FromMinutes(3);

    /// <summary>The cap on an artifact when the caller names none: the git writer's own <c>MaxPatchBytes</c> (D30).</summary>
    public const long DefaultMaxArtifactBytes = 8 * 1024 * 1024;

    /// <summary>The cap no caller may raise: an artifact is read into memory whole.</summary>
    public const long MaxArtifactBytes = 64 * 1024 * 1024;

    /// <summary>A failure that may pass: the step runs again later.</summary>
    public static bool IsTransient(Exception ex, CancellationToken ct)
        => ex is ForgeRequestException or GitWriterUnavailableException or HttpRequestException or TimeoutException
           || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    public async Task<WorkflowHookForkVerdict> CheckForkAsync(IWorkflowHookSaga saga, WorkflowHookForkCheck check, CancellationToken cancellationToken = default)
    {
        var pushed = await saga.Forge.GetWorkflowFilesAsync(saga.Repository, check.PushedRef, cancellationToken);
        var atDefaultBranch = await saga.Forge.GetWorkflowFilesAsync(saga.Repository, check.DefaultBranch, cancellationToken);
        var findings = new List<ForkGuardFinding>();
        foreach (var file in check.Workflows)
        {
            var path = WorkflowPath(file);
            if (!atDefaultBranch.Any(f => string.Equals(f.Path, path, StringComparison.Ordinal)))
                return new WorkflowHookForkVerdict(path, []);
            foreach (var finding in MergeQueueForkGuard.Evaluate(pushed, check.PushedBranch, atDefaultBranch, check.DefaultBranch, file, check.DispatchBranch))
                if (!findings.Contains(finding))
                    findings.Add(finding);
        }
        return new WorkflowHookForkVerdict(null, findings);
    }

    public async Task<WorkflowHookProvenanceFailure?> VerifyProvenanceAsync(IWorkflowHookSaga saga, string defaultBranch, string commitSha, IReadOnlyList<string> workflows, CancellationToken cancellationToken = default)
    {
        foreach (var file in workflows)
        {
            var path = WorkflowPath(file);
            var atDefault = await saga.Forge.GetFileBlobShaAsync(saga.Repository, defaultBranch, path, cancellationToken);
            if (atDefault is null)
                return new(EWorkflowHookProvenance.Missing, path);
            var atCommit = await saga.Forge.GetFileBlobShaAsync(saga.Repository, commitSha, path, cancellationToken);
            if (!string.Equals(atCommit, atDefault, StringComparison.Ordinal))
                return new(EWorkflowHookProvenance.Differs, path);
        }
        return null;
    }

    public async Task<WorkflowHookDispatch> DispatchAsync(IWorkflowHookSaga saga, WorkflowHookRequest request, Func<WorkflowHookDispatch, Task> onDispatched, CancellationToken cancellationToken = default)
    {
        await CancelStrayRunsAsync(saga, request, cancellationToken);

        var dispatch = await saga.Forge.DispatchWorkflowAsync(saga.Repository, request.Workflow, request.Ref, request.Inputs, cancellationToken);
        if (dispatch.Outcome != EForgeDispatchOutcome.Dispatched)
            return new(EWorkflowHookDispatch.Refused, request.Workflow, null, null, dispatch.Outcome, dispatch.Message);
        // D14: a 204 (no return_run_details) started a run nobody can name; any run of the workflow could be taken for it.
        if (dispatch.RunId is not { } runId)
            return new(EWorkflowHookDispatch.Uncorrelated, request.Workflow, null, dispatch.HtmlUrl, dispatch.Outcome, dispatch.Message);

        var dispatched = new WorkflowHookDispatch(EWorkflowHookDispatch.Dispatched, request.Workflow, runId, dispatch.HtmlUrl, dispatch.Outcome, null);
        await onDispatched(dispatched);
        await saga.ScheduleAsync(EWorkflowHookStep.PollRun, RunPollInterval, null, cancellationToken);
        // Per run (O36): a new generation retires the previous run's timer.
        await saga.ScheduleAsync(EWorkflowHookStep.Timeout, request.Timeout, null, cancellationToken);
        return dispatched;
    }

    /// <summary>
    /// §0 D53: a dispatch whose answer never arrived (the app died, or the response was lost) may have started a run the
    /// saga never learnt the id of, which nothing would cancel. Right before a dispatch the saga waits for no run, so any
    /// run of the workflow still going on the ref is such a stray (or an abandoned run whose cancellation is still on its
    /// way): it is cancelled rather than paid for twice. Best effort: a failure here never holds the dispatch back.
    /// </summary>
    private async Task CancelStrayRunsAsync(IWorkflowHookSaga saga, WorkflowHookRequest request, CancellationToken ct)
    {
        try
        {
            foreach (var run in await saga.Forge.ListWorkflowRunsAsync(saga.Repository, request.Workflow, request.Ref, ct))
                if (!run.IsCompleted && await saga.Forge.CancelWorkflowRunAsync(saga.Repository, run.Id, ct))
                    saga.Note($"cancelled the stray {request.Purpose} run {run.Id}, started by a dispatch whose answer never arrived");
        }
        catch (Exception ex) when (ex is ForgeAccessDeniedException || IsTransient(ex, ct))
        {
            logger.LogWarning(ex, "Workflow hook: could not look for stray runs of {Workflow} on {Branch} in {Repository}", request.Workflow, request.Ref, saga.Repository.FullName);
        }
    }

    public async Task<WorkflowHookObservation> ObserveAsync(IWorkflowHookSaga saga, WorkflowHookRun run, string expectedSha, bool fromPoll, CancellationToken cancellationToken = default)
    {
        if (run.Finished || run.RunId is not { } runId) return new(EWorkflowHookRunVerdict.Running, null);
        var observed = await saga.Forge.GetWorkflowRunAsync(saga.Repository, runId, cancellationToken);
        if (observed is null)
        {
            run.Finished = true;
            return new(EWorkflowHookRunVerdict.Missing, null);
        }
        if (!observed.IsCompleted)
        {
            if (fromPoll) await saga.ScheduleAsync(EWorkflowHookStep.PollRun, RunPollInterval, null, cancellationToken);
            return new(EWorkflowHookRunVerdict.Running, observed);
        }
        // Its own completion: whatever follows, there is nothing left to cancel (§0 D53).
        run.Finished = true;

        // D14: the run built the ref's head as of the dispatch; a run of any other commit proves nothing about ours.
        if (!string.Equals(observed.HeadSha, expectedSha, StringComparison.OrdinalIgnoreCase))
            return new(EWorkflowHookRunVerdict.WrongCommit, observed);
        if (observed.Conclusion != "success")
            return new(EWorkflowHookRunVerdict.Failed, observed);
        run.Succeeded = true;
        return new(EWorkflowHookRunVerdict.Succeeded, observed);
    }

    public async Task AbandonAsync(IWorkflowHookSaga saga, IEnumerable<WorkflowHookRun> runs, CancellationToken cancellationToken = default)
    {
        foreach (var run in runs.Where(r => !r.Finished && r.RunId is not null).ToList())
        {
            run.Finished = true;
            await saga.ScheduleAsync(EWorkflowHookStep.CancelRun, null, run.RunId, cancellationToken);
        }
    }

    public async Task CancelAbandonedRunAsync(IWorkflowHookSaga saga, string purpose, long runId, CancellationToken cancellationToken = default)
    {
        try
        {
            var cancelled = await saga.Forge.CancelWorkflowRunAsync(saga.Repository, runId, cancellationToken);
            saga.Note(cancelled ? $"cancelled the abandoned {purpose} run {runId}" : $"the abandoned {purpose} run {runId} had already finished");
        }
        catch (ForgeAccessDeniedException ex)
        {
            saga.Note($"could not cancel the abandoned {purpose} run {runId}: {ex.Message}");
            logger.LogWarning(ex, "Workflow hook: could not cancel run {RunId} in {Repository}", runId, saga.Repository.FullName);
        }
    }

    public async Task<ForgeArtifactDownload> DownloadArtifactAsync(IWorkflowHookSaga saga, long runId, string artifactName, long maxBytes = DefaultMaxArtifactBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactName);
        var cap = Math.Clamp(maxBytes, 0, MaxArtifactBytes);
        var artifact = await saga.Forge.DownloadArtifactAsync(saga.Repository, runId, artifactName, cap, cancellationToken);
        // The forge holds the cap while it reads; this holds it whatever the forge is.
        if (artifact.Outcome == EForgeArtifactOutcome.Ok && artifact.Content is { } content && content.LongLength > cap)
            return new ForgeArtifactDownload(EForgeArtifactOutcome.TooLarge, artifact.FileName, null, $"{content.LongLength} bytes, over the {cap}-byte cap");
        return artifact;
    }

    public async Task<WorkflowHookWriterJob<T>> RunWriterJobAsync<T>(IWorkflowHookSaga saga, ForgePullRequest? pullRequest, bool pushesWorkflowChanges, Func<ForgeWriterAccess, SidecarCredential, Task<T>> job, CancellationToken cancellationToken = default)
    {
        var push = await saga.Forge.GetWriterAccessAsync(saga.Repository, pullRequest, pushesWorkflowChanges, cancellationToken);
        if (push.Access is not { } access) return new(false, default, push.RefusalReason);
        try
        {
            return new(true, await job(access, new SidecarCredential(access.Username, access.Password)), null);
        }
        finally
        {
            // Bounded inside, and never thrown from: the job's own outcome is what the saga acts on.
            await saga.Forge.RevokeWriterAccessAsync(saga.Repository, access, CancellationToken.None);
        }
    }

    private static string WorkflowPath(string file) => $".github/workflows/{file}";
}
