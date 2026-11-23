using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.MergeQueue;
using CodeCoverage.Onboarding;
using CodeCoverage.WorkflowHooks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Notifications;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Formatting;

/// <summary>One step of one format run (#657 M4), queued through the outbox and gated on its generation.</summary>
[MessageQueue(CoverageQueues.Versioning)]
public sealed record FormatRunStepMessage
{
    public required string RepositoryId { get; init; }

    /// <summary><see cref="FormatRun.Mode"/>.</summary>
    public required string Mode { get; init; }

    public required int Generation { get; init; }
    public required EFormatRunStep Step { get; init; }

    /// <summary><see cref="EFormatRunStep.CancelRun"/> and <see cref="EFormatRunStep.RunCompleted"/>: the run.</summary>
    public long? RunId { get; init; }
}

public enum EFormatRunStep
{
    /// <summary>D1's check (the default branch has <c>formatting.yml</c>), then the dispatch.</summary>
    Dispatch,

    /// <summary>Fallback read of the run, for a lost <c>workflow_run</c> webhook.</summary>
    PollRun,

    /// <summary>The run's <c>workflow_run</c> webhook.</summary>
    RunCompleted,

    /// <summary>The run's deadline, which is also the <see cref="FormatRun"/>'s expiry.</summary>
    Timeout,

    /// <summary>Cancel a run nobody waits for any more (merge-queue PRD §0 D53). Not gated.</summary>
    CancelRun,
}

public partial class FormatRunRecipient : IRecipient<FormatRunStepMessage>
{
    [Inject] private readonly IFormatRunEngine engine;

    public Task HandleAsync(FormatRunStepMessage message, CancellationToken cancellationToken = default)
        => engine.HandleStepAsync(message, cancellationToken);
}

/// <summary>A finished workflow run that a pending format run waits for, forwarded onto its lane.</summary>
public partial class FormatRunEventsRecipient : IRecipient<ForgeWebhookMessage<CiRunCompleted>>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;

    public async Task HandleAsync(ForgeWebhookMessage<CiRunCompleted> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (!evt.RepositoryId.StartsWith("Repositories/", StringComparison.OrdinalIgnoreCase)) return;
        var pending = await session.LoadAsync<FormatRuns>(FormatRuns.ForRepository(evt.RepositoryId), cancellationToken);
        if (pending?.ForRun(evt.RunId) is not { } run) return;
        await messageBus.BroadcastAsync(new FormatRunStepMessage
        {
            RepositoryId = evt.RepositoryId, Mode = run.Mode, Generation = run.Generation, Step = EFormatRunStep.RunCompleted, RunId = evt.RunId,
        }, cancellationToken);
    }
}

/// <summary>
/// A FormatRepository or LintRepository run (#657 D3, D14): <c>formatting.yml</c> is dispatched on the default branch, its
/// <c>merge-queue-patch</c> is applied by the git writer under the format guard (D15) and opened as a pull request (D6, D7),
/// handed to the merge queue when the requester chose so (D5), and the requester is notified of whatever happened.
/// </summary>
public interface IFormatRunEngine
{
    /// <summary>Records <paramref name="run"/> and queues its dispatch, in one <c>SaveChanges</c>. False when the repository
    /// already has a run of that mode going.</summary>
    Task<bool> StartAsync(Repository repository, FormatRun run, CancellationToken cancellationToken = default);

    Task HandleStepAsync(FormatRunStepMessage step, CancellationToken cancellationToken = default);
}

/// <summary>How a format run ended, for the notification and the tests.</summary>
public enum EFormatRunOutcome
{
    Opened,
    AlreadyFormatted,
    Failed,
}

/// <inheritdoc cref="IFormatRunEngine"/>
[Register(typeof(IFormatRunEngine), ServiceLifetime.Scoped)]
public partial class FormatRunEngine : IFormatRunEngine
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly IOnboardingService onboarding;
    [Inject] private readonly IGitWriterSidecar writer;
    [Inject] private readonly IMessageOutbox outbox;
    [Inject] private readonly IWorkflowHook hook;
    [Inject] private readonly ISparkNotifier notifier;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly IOptions<MergeQueueOptions> options;
    [Inject] private readonly ILogger<FormatRunEngine> logger;

    public const int MaxTransientRetries = 6;
    public const string Purpose = "format";

    /// <summary>A run older than this is replaced by a new request rather than refusing it (its timeout step was lost).</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromHours(2);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private sealed record Context(Repository Repository, IForgeMergeQueue Queue, FormatRuns Document, FormatRun Run);

    public async Task<bool> StartAsync(Repository repository, FormatRun run, CancellationToken cancellationToken = default)
    {
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        var id = FormatRuns.ForRepository(repository.Id!);
        var document = await session.LoadAsync<FormatRuns>(id, cancellationToken);
        if (document is null)
        {
            document = new FormatRuns { Id = id };
            await session.StoreAsync(document, id, cancellationToken);
        }
        if (document.Pending.FirstOrDefault(p => p.Mode == run.Mode) is { } going)
        {
            if (Now - going.StartedUtc < Stale)
                return false;
            document.Pending.Remove(going);
        }
        run.StartedUtc = Now;
        run.Note(Now, $"requested on {run.Branch} at {Short(run.Sha)}");
        document.Pending.Add(run);
        await ScheduleAsync(repository.Id!, run, EFormatRunStep.Dispatch, null, null, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task HandleStepAsync(FormatRunStepMessage step, CancellationToken cancellationToken = default)
    {
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        var ct = cancellationToken;
        var repository = await session.LoadAsync<Repository>(step.RepositoryId, ct);
        if (repository is null || queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } queue)
            return;
        if (step.Step == EFormatRunStep.CancelRun)
        {
            if (step.RunId is { } cancel)
                await hook.CancelAbandonedRunAsync(new CancelSaga(repository, queue), Purpose, cancel, ct);
            return;
        }
        var document = await session.LoadAsync<FormatRuns>(FormatRuns.ForRepository(step.RepositoryId), ct);
        if (document?.Pending.FirstOrDefault(p => p.Mode == step.Mode) is not { } run || run.Generation != step.Generation)
        {
            logger.LogDebug("Format run: ignoring {Step} for {Mode} in {Repository} (generation {Generation})", step.Step, step.Mode, repository.FullName, step.Generation);
            return;
        }
        var context = new Context(repository, queue, document, run);

        try
        {
            await (step.Step switch
            {
                EFormatRunStep.Dispatch => DispatchAsync(context, ct),
                EFormatRunStep.PollRun => OnRunAsync(context, fromPoll: true, ct),
                EFormatRunStep.RunCompleted => OnRunAsync(context, fromPoll: false, ct),
                EFormatRunStep.Timeout => FinishAsync(context, EFormatRunOutcome.Failed, $"the `formatting.yml` run took longer than {run.TimeoutMinutes} minutes", null, ct),
                _ => Task.CompletedTask,
            });
        }
        catch (Exception ex) when (ForgeRateLimitedException.Find(ex) is not null)
        {
            throw; // The message bus waits out a rate limit; nothing of this attempt is saved.
        }
        catch (ForgeAccessDeniedException ex)
        {
            await FinishAsync(context, EFormatRunOutcome.Failed, $"the forge refused a call the run needs{(ex.RequiredPermission is { } p ? $" (it asks for `{p}`)" : string.Empty)}", null, ct);
        }
        catch (GitWriterRefusedException ex)
        {
            await FinishAsync(context, EFormatRunOutcome.Failed, $"the git writer refused the {ex.Job} request (`{ex.Field}` must be {ex.Rule})", null, ct);
        }
        catch (Exception ex) when (WorkflowHook.IsTransient(ex, ct))
        {
            run.TransientFailures++;
            if (run.TransientFailures > MaxTransientRetries)
            {
                await FinishAsync(context, EFormatRunOutcome.Failed, $"it gave up after {run.TransientFailures} failed attempts in a row ({ex.Message})", null, ct);
            }
            else
            {
                var delay = TimeSpan.FromSeconds(Math.Min(600, 15 * Math.Pow(2, run.TransientFailures - 1)));
                logger.LogWarning(ex, "Format run: {Step} of {Mode} in {Repository} failed; retrying in {Delay}", step.Step, run.Mode, repository.FullName, delay);
                await ScheduleAsync(repository.Id!, run, step.Step, delay, step.RunId, ct);
            }
        }
        await session.SaveChangesAsync(ct);
    }

    /// <summary>D1: the default branch's own <c>formatting.yml</c>, then the dispatch on that branch.</summary>
    private async Task DispatchAsync(Context context, CancellationToken ct)
    {
        var run = context.Run;
        if (run.Run is not null) return;
        if (!writer.IsConfigured)
        {
            await FinishAsync(context, EFormatRunOutcome.Failed, "this server's git writer is not configured", null, ct);
            return;
        }
        var saga = new Saga(this, context);
        if (await hook.VerifyProvenanceAsync(saga, run.Branch, run.Sha, [FormattingWorkflow.FileName], ct) is { } failure)
        {
            await FinishAsync(context, EFormatRunOutcome.Failed, failure.Kind == EWorkflowHookProvenance.Missing
                ? $"`{FormattingWorkflow.Path}` does not exist on `{run.Branch}`; set up Formatting first"
                : $"`{failure.Path}` differs from `{run.Branch}`'s copy", null, ct);
            return;
        }

        var request = new WorkflowHookRequest(FormattingWorkflow.FileName, run.Branch, new Dictionary<string, string>
        {
            ["mode"] = run.Mode,
            ["expected_sha"] = run.Sha,
        }, TimeSpan.FromMinutes(run.TimeoutMinutes), Purpose);
        var dispatch = await hook.DispatchAsync(saga, request, dispatched =>
        {
            // A new generation retires the dispatch step and anything queued before it.
            run.Generation++;
            run.TransientFailures = 0;
            run.Run = new FormatRunWorkflow { Workflow = FormattingWorkflow.FileName, RunId = dispatched.RunId, RunUrl = dispatched.RunUrl };
            run.Note(Now, $"dispatched {FormattingWorkflow.FileName} run {dispatched.RunId} on {Short(run.Sha)}");
            return Task.CompletedTask;
        }, ct);
        switch (dispatch.Outcome)
        {
            case EWorkflowHookDispatch.Refused:
                await FinishAsync(context, EFormatRunOutcome.Failed, $"the forge refused to run `{FormattingWorkflow.FileName}` on `{run.Branch}` ({dispatch.ForgeOutcome}: {dispatch.Message})", null, ct);
                return;
            case EWorkflowHookDispatch.Uncorrelated:
                await FinishAsync(context, EFormatRunOutcome.Failed, $"the forge started `{FormattingWorkflow.FileName}` without saying which run it is, so its result cannot be trusted", null, ct);
                return;
        }
    }

    private async Task OnRunAsync(Context context, bool fromPoll, CancellationToken ct)
    {
        var run = context.Run;
        if (run.Run is not { Finished: false, RunId: { } runId } pending) return;
        var saga = new Saga(this, context);
        var observation = await hook.ObserveAsync(saga, pending, run.Sha, fromPoll, ct);
        var forgeRun = observation.Run;
        switch (observation.Verdict)
        {
            case EWorkflowHookRunVerdict.Running:
                return;
            case EWorkflowHookRunVerdict.Missing:
                await FinishAsync(context, EFormatRunOutcome.Failed, $"the `formatting.yml` run {runId} no longer exists", null, ct);
                return;
            case EWorkflowHookRunVerdict.WrongCommit:
                await FinishAsync(context, EFormatRunOutcome.Failed, $"the run formatted `{Short(forgeRun!.HeadSha)}`, but `{run.Branch}` was at `{Short(run.Sha)}`; `{run.Branch}` moved, run it again", null, ct);
                return;
            case EWorkflowHookRunVerdict.Failed:
                await FinishAsync(context, EFormatRunOutcome.Failed, $"the `formatting.yml` run finished `{forgeRun!.Conclusion ?? "without a conclusion"}`", forgeRun.HtmlUrl ?? pending.RunUrl, ct);
                return;
        }

        var artifact = await hook.DownloadArtifactAsync(saga, runId, options.Value.PatchArtifactName, options.Value.MaxPatchBytes, ct);
        if (artifact.Outcome != EForgeArtifactOutcome.Ok)
        {
            await FinishAsync(context, EFormatRunOutcome.Failed, $"the run left an unusable `{options.Value.PatchArtifactName}` artifact ({artifact.Outcome}: {artifact.Detail})", pending.RunUrl, ct);
            return;
        }
        if (artifact.Content is not { Length: > 0 } patch)
        {
            await FinishAsync(context, EFormatRunOutcome.AlreadyFormatted, null, pending.RunUrl, ct);
            return;
        }

        // D15, before any token is asked for: the writer checks again, and alone applies.
        var verdict = FormatPatchValidator.Validate(patch, run.Exclude, options.Value.MaxPatchBytes);
        if (!verdict.Accepted)
        {
            await FinishAsync(context, EFormatRunOutcome.Failed, $"its patch was refused ({verdict.Reason}{(verdict.Paths.Count > 0 ? $" on `{verdict.Paths[0]}`" : "")}{(verdict.Detail is { } d ? $": {d}" : "")})", pending.RunUrl, ct);
            return;
        }
        var report = await hook.DownloadArtifactAsync(saga, runId, FormattingWorkflow.ReportArtifactName, FormattingWorkflow.MaxReportBytes, ct);
        var body = FormattingWorkflow.Body(run.Mode, pending.RunUrl, report.Outcome == EForgeArtifactOutcome.Ok ? report.Content : null, verdict.Paths.Count, run.Enqueue);
        var opened = await onboarding.FormattingPullRequestAsync(context.Repository, new FormattingCommit(run.Mode, run.Sha, FormattingWorkflow.Title(run.Mode), body,
            patch, run.Exclude, FormatPatchValidator.TouchesGitHub(verdict)), ct);
        if (!opened.Opened)
        {
            await FinishAsync(context, EFormatRunOutcome.Failed, opened.Note, pending.RunUrl, ct);
            return;
        }
        if (run.Enqueue && opened.Number is { } number && run.RequestedByLogin is { Length: > 0 } actor)
        {
            await outbox.EnqueueAsync(session, new MergeQueueSignalMessage
            {
                RepositoryId = context.Repository.Id!, Kind = EMergeQueueSignal.Command, Number = number, Command = EMergeQueueCommand.Enqueue,
                Actor = actor, Source = EMergeQueueCommandSource.Formatting,
            }, null, ct);
        }
        await FinishAsync(context, EFormatRunOutcome.Opened, opened.Note, opened.Url, ct);
    }

    /// <summary>Notifies the requester (D14) and forgets the run. A run still going is abandoned (D53).</summary>
    private async Task FinishAsync(Context context, EFormatRunOutcome outcome, string? why, string? url, CancellationToken ct)
    {
        var run = context.Run;
        if (run.Run is { } workflow)
            await hook.AbandonAsync(new Saga(this, context), [workflow], ct);
        var what = run.Mode == FormattingWorkflow.LintMode ? "Lint" : "Format";
        var name = context.Repository.FullName;
        var (title, text) = outcome switch
        {
            EFormatRunOutcome.Opened => ($"{what} of {name}: pull request opened", $"{why}{(run.Enqueue ? ", handed to the merge queue" : "")}."),
            EFormatRunOutcome.AlreadyFormatted => ($"{what} of {name}: nothing to change",
                run.Mode == FormattingWorkflow.LintMode ? "The linters had nothing to fix." : "The repository is already formatted."),
            _ => ($"{what} of {name}: no pull request", $"{Capitalize(why ?? "the run failed")}."),
        };
        run.Note(Now, $"{outcome}: {text}");
        logger.LogInformation("Format run: {Mode} of {Repository} ended {Outcome}: {Text}", run.Mode, name, outcome, text);
        if (run.RequestedBy is { Length: > 0 } user)
        {
            await notifier.NotifyAsync(new SparkNotification
            {
                UserIds = [user], Title = Cut(title, 200), Body = Cut(text, 2000), Url = url is { Length: > 0 } && Uri.TryCreate(url, UriKind.Absolute, out _) ? url : null,
                Category = "format-run",
            }, ct);
        }
        context.Document.Pending.Remove(run);
    }

    private Task ScheduleAsync(string repositoryId, FormatRun run, EFormatRunStep step, TimeSpan? delay, long? runId, CancellationToken ct)
        => outbox.EnqueueAsync(session, new FormatRunStepMessage
        {
            RepositoryId = repositoryId, Mode = run.Mode, Generation = run.Generation, Step = step, RunId = runId,
        }, delay is null ? null : new BroadcastOptions { Delay = delay }, ct);

    private static string Short(string? sha) => sha is { Length: > 12 } ? sha[..12] : sha ?? "?";

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private sealed class Saga(FormatRunEngine engine, Context context) : IWorkflowHookSaga
    {
        public Repository Repository => context.Repository;

        public IForgeMergeQueue Forge => context.Queue;

        public void Note(string text) => context.Run.Note(engine.Now, text);

        public Task ScheduleAsync(EWorkflowHookStep step, TimeSpan? delay, long? runId, CancellationToken cancellationToken) => step switch
        {
            EWorkflowHookStep.PollRun => engine.ScheduleAsync(context.Repository.Id!, context.Run, EFormatRunStep.PollRun, delay, null, cancellationToken),
            EWorkflowHookStep.Timeout => engine.ScheduleAsync(context.Repository.Id!, context.Run, EFormatRunStep.Timeout, delay, null, cancellationToken),
            EWorkflowHookStep.CancelRun => engine.ScheduleAsync(context.Repository.Id!, context.Run, EFormatRunStep.CancelRun, null, runId, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
        };
    }

    private sealed class CancelSaga(Repository repository, IForgeMergeQueue queue) : IWorkflowHookSaga
    {
        public Repository Repository => repository;

        public IForgeMergeQueue Forge => queue;

        public void Note(string text) { }

        public Task ScheduleAsync(EWorkflowHookStep step, TimeSpan? delay, long? runId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
