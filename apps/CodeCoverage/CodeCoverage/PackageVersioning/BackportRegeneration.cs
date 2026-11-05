using System.Globalization;
using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.Git;
using CodeCoverage.Git.MergeQueue;
using CodeCoverage.MergeQueue;
using CodeCoverage.WorkflowHooks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.PackageVersioning;

/// <summary>One step of one backport's regeneration (#570 M6), queued through the outbox and gated on its generation.</summary>
[MessageQueue(CoverageQueues.Versioning)]
public sealed record BackportRegenerationStepMessage
{
    public required string RepositoryId { get; init; }

    /// <summary>The backport branch (<see cref="BackportRegeneration.Head"/>).</summary>
    public required string Head { get; init; }

    public required int Generation { get; init; }
    public required EBackportRegenerationStep Step { get; init; }

    /// <summary><see cref="EBackportRegenerationStep.CancelRun"/> and <see cref="EBackportRegenerationStep.RunCompleted"/>: the run.</summary>
    public long? RunId { get; init; }
}

public enum EBackportRegenerationStep
{
    /// <summary>Dispatch the next workflow; before the first, D12's provenance against the line's tip.</summary>
    Dispatch,

    /// <summary>Fallback read of the pending run, for a lost <c>workflow_run</c> webhook.</summary>
    PollRun,

    /// <summary>The pending run's <c>workflow_run</c> webhook.</summary>
    RunCompleted,

    /// <summary>The pending run's deadline.</summary>
    Timeout,

    /// <summary>Cancel a run the backport stopped waiting for (§0 D53). Not gated.</summary>
    CancelRun,
}

public partial class BackportRegenerationRecipient : IRecipient<BackportRegenerationStepMessage>
{
    [Inject] private readonly IBackportRegenerationEngine engine;

    public Task HandleAsync(BackportRegenerationStepMessage message, CancellationToken cancellationToken = default)
        => engine.HandleStepAsync(message, cancellationToken);
}

/// <summary>A finished workflow run that a pending backport waits for, forwarded onto the versioning lane.</summary>
public partial class BackportRegenerationEventsRecipient : IRecipient<ForgeWebhookMessage<CiRunCompleted>>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;

    public async Task HandleAsync(ForgeWebhookMessage<CiRunCompleted> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (!evt.RepositoryId.StartsWith("Repositories/", StringComparison.OrdinalIgnoreCase)) return;
        var pending = await session.LoadAsync<BackportRegenerations>(BackportRegenerations.ForRepository(evt.RepositoryId), cancellationToken);
        if (pending?.ForRun(evt.RunId) is not { } item) return;
        await messageBus.BroadcastAsync(new BackportRegenerationStepMessage
        {
            RepositoryId = evt.RepositoryId, Head = item.Head, Generation = item.Generation, Step = EBackportRegenerationStep.RunCompleted, RunId = evt.RunId,
        }, cancellationToken);
    }
}

/// <summary>
/// A backport's regeneration (#570 M6, PRD D9): the backport branch is pushed with its regenerated files at the line's
/// side; the line's regenerate workflow runs on it, its patch is folded into the backport commit by the git writer, and
/// only then does the backport's pull request open. Whatever stops it opens the pull request anyway, with a note saying
/// which files were not regenerated and why.
/// </summary>
public interface IBackportRegenerationEngine
{
    /// <summary>Records <paramref name="item"/> and queues its first dispatch, in one <c>SaveChanges</c>.</summary>
    Task BeginAsync(Repository repository, BackportRegeneration item, CancellationToken cancellationToken = default);

    Task HandleStepAsync(BackportRegenerationStepMessage step, CancellationToken cancellationToken = default);
}

/// <summary>What a backport's regeneration runs, or why it cannot.</summary>
/// <param name="Globs">The globs a patch may touch (T4): <c>regeneratedPaths</c> plus each concrete path outside them.</param>
public sealed record BackportRegenerationPlan(IReadOnlyList<string> Workflows, IReadOnlyList<string> Globs, string? Refusal);

/// <inheritdoc cref="IBackportRegenerationEngine"/>
/// <remarks>
/// <para><b>Provenance</b> (owner, 2026-11-05): the dispatched run executes the workflow file of the backport branch, so
/// each workflow there must be byte for byte the <b>line tip's</b> copy. A backport that edits its own regenerate
/// workflow is not regenerated.</para>
/// <para><b>Serial</b>: the versioning lane, optimistic concurrency, and every step gated on
/// <see cref="BackportRegeneration.Generation"/>. Workflows run one after the other, each on the commit the previous
/// fold left.</para>
/// </remarks>
[Register(typeof(IBackportRegenerationEngine), ServiceLifetime.Scoped)]
public partial class BackportRegenerationEngine : IBackportRegenerationEngine
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly IGitWriterSidecar writer;
    [Inject] private readonly IMessageOutbox outbox;
    [Inject] private readonly IWorkflowHook hook;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly IOptions<MergeQueueOptions> options;
    [Inject] private readonly ILogger<BackportRegenerationEngine> logger;

    public const int MaxTransientRetries = 6;

    /// <summary>Who the merge queue records as having queued a backport (#570 M7); the App, as a writer (<see cref="EMergeQueueCommandSource.Backports"/>).</summary>
    public const string QueueActor = "mintplayer-backports";

    /// <summary>Hands an opened backport pull request to the merge queue (#570 M7, D12); the queue re-checks everything.</summary>
    internal static Task EnqueueAsync(IMessageOutbox outbox, IAsyncDocumentSession session, string repositoryId, int number, CancellationToken ct)
        => outbox.EnqueueAsync(session, new MergeQueueSignalMessage
        {
            RepositoryId = repositoryId, Kind = EMergeQueueSignal.Command, Number = number, Command = EMergeQueueCommand.Enqueue,
            Actor = QueueActor, Source = EMergeQueueCommandSource.Backports,
        }, null, ct);
    public const string Purpose = "backport regenerate";

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private sealed record Context(Repository Repository, IForgeVersioning Forge, IForgeMergeQueue Queue, BackportRegenerations Document, BackportRegeneration Item);

    /// <summary>
    /// The workflows a target's regeneration runs: for a <c>regenerate</c> resolution its rule's <c>action</c>, else
    /// <c>postRebaseActionName</c>, as the merge queue picks them (O36). Refused under <c>postRebaseWhen: never</c>, with no
    /// workflow, or with more globs than the writer takes.
    /// </summary>
    public static BackportRegenerationPlan Plan(MergeQueueSection? section, GitWriterBackportTargetResult target)
    {
        var paths = target.Regenerate ?? [];
        if (section is null)
            return new([], [], "the default branch's `coverage.yml` has no usable `mergeQueue` section");
        if (section.PostRebaseWhen == EPostRebaseWhen.Never)
            return new([], [], "`mergeQueue.postRebaseWhen` is `never`");

        var workflows = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var rule = target.Resolutions?.FirstOrDefault(r => r.Path == path && r.Regenerate)?.Rule ?? -1;
            var workflow = rule >= 0 && rule < section.AutoResolve.Count ? section.AutoResolve[rule].Action ?? section.PostRebaseActionName : section.PostRebaseActionName;
            if (!string.IsNullOrEmpty(workflow)) workflows.Add(workflow);
        }
        if (workflows.Count == 0)
            return new([], [], "`mergeQueue.postRebaseActionName` names no regenerate workflow");

        // As the merge queue's publish (O37): the list, then each concrete path it does not already allow.
        var globs = new List<string>(section.RegeneratedPaths);
        var matchers = section.RegeneratedPaths.Where(MergeQueueWireRules.IsGlob).Select(g => new PathGlob(g)).ToList();
        foreach (var path in paths)
        {
            if (!MergeQueueWireRules.IsGlob(path) || path.IndexOfAny(['*', '?', '[']) >= 0
                || globs.Contains(path, StringComparer.Ordinal) || matchers.Any(g => g.Matches(path)))
                continue;
            globs.Add(path);
        }
        if (globs.Count > MergeQueueWireRules.MaxGlobs)
            return new([], [], $"its {globs.Count} regenerated paths exceed the git writer's {MergeQueueWireRules.MaxGlobs}");
        return new([.. workflows], globs, null);
    }

    /// <summary>The note on a backport pull request whose files were not regenerated.</summary>
    public static string NotRegeneratedNote(IEnumerable<string> paths, string why)
    {
        var builder = new StringBuilder().Append(CultureInfo.InvariantCulture, $"\n\n**Not regenerated**: {why}. These files kept the line's version and need regenerating on this branch:\n");
        foreach (var path in paths.Take(50))
            builder.Append(CultureInfo.InvariantCulture, $"- `{path}`\n");
        return builder.ToString();
    }

    private static string RegeneratedNote(BackportRegeneration item)
    {
        var builder = new StringBuilder().Append(CultureInfo.InvariantCulture,
            $"\n\nRegenerated on this line by {string.Join(", ", item.Workflows.Select(w => $"`{w}`"))}, folded into the backport commit:\n");
        var changed = item.Regenerated.Distinct(StringComparer.Ordinal).ToList();
        if (changed.Count == 0)
            builder.Append("- nothing changed\n");
        foreach (var path in changed.Take(50))
            builder.Append(CultureInfo.InvariantCulture, $"- `{path}`\n");
        return builder.ToString();
    }

    public async Task BeginAsync(Repository repository, BackportRegeneration item, CancellationToken cancellationToken = default)
    {
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        var id = BackportRegenerations.ForRepository(repository.Id!);
        var document = await session.LoadAsync<BackportRegenerations>(id, cancellationToken);
        if (document is null)
        {
            document = new BackportRegenerations { Id = id };
            await session.StoreAsync(document, id, cancellationToken);
        }
        document.Pending.RemoveAll(p => p.Head == item.Head);
        document.Pending.Add(item);
        item.Note(Now, $"pushed {Short(item.Sha)}; regenerating {item.Paths.Count} file{(item.Paths.Count == 1 ? "" : "s")} with {string.Join(", ", item.Workflows)}");
        await ScheduleAsync(repository.Id!, item, EBackportRegenerationStep.Dispatch, null, null, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }

    public async Task HandleStepAsync(BackportRegenerationStepMessage step, CancellationToken cancellationToken = default)
    {
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        var ct = cancellationToken;
        var repository = await session.LoadAsync<Repository>(step.RepositoryId, ct);
        if (repository is null
            || forges.FirstOrDefault(f => f.Provider == repository.Provider) is not { } forge
            || queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } queue)
            return;
        var document = await session.LoadAsync<BackportRegenerations>(BackportRegenerations.ForRepository(step.RepositoryId), ct);

        if (step.Step == EBackportRegenerationStep.CancelRun)
        {
            // Not gated: the backport it belonged to may be gone already.
            if (step.RunId is { } cancel)
                await hook.CancelAbandonedRunAsync(new CancelSaga(repository, queue), Purpose, cancel, ct);
            return;
        }
        if (document?.Pending.FirstOrDefault(p => p.Head == step.Head) is not { } item || item.Generation != step.Generation)
        {
            logger.LogDebug("Backport regeneration: ignoring {Step} for {Head} in {Repository} (generation {Generation})", step.Step, step.Head, repository.FullName, step.Generation);
            return;
        }
        var context = new Context(repository, forge, queue, document, item);

        try
        {
            await (step.Step switch
            {
                EBackportRegenerationStep.Dispatch => DispatchAsync(context, ct),
                EBackportRegenerationStep.PollRun => OnRunAsync(context, fromPoll: true, ct),
                EBackportRegenerationStep.RunCompleted => OnRunAsync(context, fromPoll: false, ct),
                EBackportRegenerationStep.Timeout => TimeoutAsync(context, ct),
                _ => Task.CompletedTask,
            });
        }
        catch (Exception ex) when (ForgeRateLimitedException.Find(ex) is not null)
        {
            throw; // The message bus waits out a rate limit; nothing of this attempt is saved.
        }
        catch (ForgeAccessDeniedException ex)
        {
            await FinishAsync(context, $"the forge refused a call the regeneration needs{(ex.RequiredPermission is { } p ? $" (it asks for `{p}`)" : string.Empty)}", ct);
        }
        catch (GitWriterRefusedException ex)
        {
            await FinishAsync(context, $"the git writer refused the {ex.Job} request (`{ex.Field}` must be {ex.Rule})", ct);
        }
        catch (Exception ex) when (WorkflowHook.IsTransient(ex, ct))
        {
            item.TransientFailures++;
            if (item.TransientFailures > MaxTransientRetries)
            {
                await FinishAsync(context, $"it gave up after {item.TransientFailures} failed attempts in a row ({ex.Message})", ct);
            }
            else
            {
                var delay = TimeSpan.FromSeconds(Math.Min(600, 15 * Math.Pow(2, item.TransientFailures - 1)));
                logger.LogWarning(ex, "Backport regeneration: {Step} of {Head} in {Repository} failed; retrying in {Delay}", step.Step, item.Head, repository.FullName, delay);
                await ScheduleAsync(repository.Id!, item, step.Step, delay, step.RunId, ct);
            }
        }
        await session.SaveChangesAsync(ct);
    }

    /// <summary>The next workflow; before the first, D12 against the line's tip (owner, 2026-11-05).</summary>
    private async Task DispatchAsync(Context context, CancellationToken ct)
    {
        var item = context.Item;
        if (item.Runs.Any(r => !r.Finished)) return;
        if (item.Runs.Count >= item.Workflows.Count)
        {
            await FinishAsync(context, null, ct);
            return;
        }
        if (!writer.IsConfigured)
        {
            await FinishAsync(context, "this server's git writer is not configured", ct);
            return;
        }
        var saga = new Saga(this, context);
        if (item.Runs.Count == 0)
        {
            switch (await hook.VerifyProvenanceAsync(saga, item.Branch, item.Sha, item.Workflows, ct))
            {
                case { Kind: EWorkflowHookProvenance.Missing, Path: var missing }:
                    await FinishAsync(context, $"`{missing}` does not exist on `{item.Branch}`", ct);
                    return;
                case { Kind: EWorkflowHookProvenance.Differs, Path: var differs }:
                    await FinishAsync(context, $"the backport changes `{differs}`, and only `{item.Branch}`'s own copy is run", ct);
                    return;
            }
        }

        var file = item.Workflows[item.Runs.Count];
        var request = new WorkflowHookRequest(file, item.Head, new Dictionary<string, string>
        {
            ["expected_sha"] = item.Sha,
            ["pr_number"] = item.Number.ToString(CultureInfo.InvariantCulture),
            ["queue_run_id"] = $"{item.Head.Replace('/', '-')}.{item.Runs.Count + 1}",
        }, TimeSpan.FromMinutes(item.PostRebaseTimeoutMinutes), Purpose);
        var dispatch = await hook.DispatchAsync(saga, request, dispatched =>
        {
            // A new generation retires the previous run's timer and poll chain.
            item.Generation++;
            item.TransientFailures = 0;
            item.Runs.Add(new BackportRegenerationRun { Workflow = file, RunId = dispatched.RunId, RunUrl = dispatched.RunUrl });
            item.Note(Now, $"dispatched {file} run {dispatched.RunId} on {Short(item.Sha)}");
            return Task.CompletedTask;
        }, ct);
        switch (dispatch.Outcome)
        {
            case EWorkflowHookDispatch.Refused:
                await FinishAsync(context, $"the forge refused to run `{file}` on `{item.Head}` ({dispatch.ForgeOutcome}: {dispatch.Message})", ct);
                return;
            case EWorkflowHookDispatch.Uncorrelated:
                await FinishAsync(context, $"the forge started `{file}` without saying which run it is, so its result cannot be trusted", ct);
                return;
        }
    }

    private async Task OnRunAsync(Context context, bool fromPoll, CancellationToken ct)
    {
        var item = context.Item;
        if (item.Runs.LastOrDefault() is not { Finished: false, RunId: { } runId } pending) return;
        var saga = new Saga(this, context);
        var observation = await hook.ObserveAsync(saga, pending, item.Sha, fromPoll, ct);
        var run = observation.Run;
        switch (observation.Verdict)
        {
            case EWorkflowHookRunVerdict.Running:
                return;
            case EWorkflowHookRunVerdict.Missing:
                await FinishAsync(context, $"the `{pending.Workflow}` run {runId} no longer exists", ct);
                return;
            case EWorkflowHookRunVerdict.WrongCommit:
                await FinishAsync(context, $"the `{pending.Workflow}` run built `{Short(run!.HeadSha)}`, not the backport's `{Short(item.Sha)}`", ct);
                return;
            case EWorkflowHookRunVerdict.Failed:
                await FinishAsync(context, $"the `{pending.Workflow}` run finished `{run!.Conclusion ?? "without a conclusion"}`{(run.HtmlUrl is { } url ? $" ({url})" : "")}", ct);
                return;
        }

        var artifact = await hook.DownloadArtifactAsync(saga, runId, options.Value.PatchArtifactName, options.Value.MaxPatchBytes, ct);
        if (artifact.Outcome != EForgeArtifactOutcome.Ok)
        {
            await FinishAsync(context, $"the `{pending.Workflow}` run left an unusable `{options.Value.PatchArtifactName}` artifact ({artifact.Outcome}: {artifact.Detail})", ct);
            return;
        }
        if (artifact.Content is { Length: > 0 } patch)
        {
            var repository = context.Repository;
            var job = await hook.RunWriterJobAsync(saga, pullRequest: null, pushesWorkflowChanges: false, (access, credential) => writer.VersioningBackportRegeneratedAsync(
                new GitWriterBackportRegeneratedRequest(repository.Provider.ToCanonicalString(), repository.GitHubId.ToString(CultureInfo.InvariantCulture), access.CloneUrl,
                    credential, item.Head, item.Sha, Convert.ToBase64String(patch), item.RegeneratedPaths, VersioningController.Tagger), ct), ct);
            if (!job.Ran || job.Result is not { } result)
            {
                await FinishAsync(context, $"the forge gave no token to push the regenerated files{(job.Refusal is { } refusal ? $" ({refusal})" : "")}", ct);
                return;
            }
            if (result.Outcome != GitWriterBackportRegeneratedResult.Regenerated || result.Commit is not { } commit)
            {
                var paths = result.Paths is { Count: > 0 } refused ? $" on {string.Join(", ", refused.Take(10).Select(p => $"`{p}`"))}" : "";
                await FinishAsync(context, $"the git writer did not fold the `{pending.Workflow}` patch ({result.Reason ?? result.Outcome}{paths}){(result.Detail is { Length: > 0 } d ? $": {d}" : "")}", ct);
                return;
            }
            item.Note(Now, $"folded the {pending.Workflow} patch into {Short(commit)}");
            item.Sha = commit;
            item.Regenerated.AddRange(result.Paths ?? []);
        }
        else
        {
            item.Note(Now, $"{pending.Workflow} run {runId} had nothing to regenerate");
        }

        // The next workflow runs on the commit this one left.
        item.Generation++;
        await ScheduleAsync(context.Repository.Id!, item, EBackportRegenerationStep.Dispatch, null, null, ct);
    }

    private Task TimeoutAsync(Context context, CancellationToken ct)
        => FinishAsync(context, $"the regenerate run took longer than {context.Item.PostRebaseTimeoutMinutes} minutes", ct);

    /// <summary>
    /// Opens the backport's pull request, with what was regenerated or why not (<paramref name="why"/>), comments on the
    /// backported pull request, and forgets the backport. A run still going is abandoned (D53).
    /// </summary>
    private async Task FinishAsync(Context context, string? why, CancellationToken ct)
    {
        var item = context.Item;
        await hook.AbandonAsync(new Saga(this, context), item.Runs, ct);
        var body = item.Body + (why is null ? RegeneratedNote(item) : NotRegeneratedNote(item.Paths, why));
        var opened = await context.Forge.OpenPullRequestAsync(context.Repository, item.Head, item.Branch, item.Title, body, cancellationToken: ct);
        // #570 M7: only a regenerated backport is queued; stale files wait for a person.
        var enqueue = why is null && item.Enqueue;
        if (enqueue)
            await EnqueueAsync(outbox, session, context.Repository.Id!, opened, ct);
        await context.Forge.CommentAsync(context.Repository, item.Number, why is null
            ? $"Backport to `{item.Branch}`: #{opened}, regenerated by {string.Join(", ", item.Workflows.Select(w => $"`{w}`"))}{(enqueue ? ", handed to the merge queue" : "")}."
            : $"Backport to `{item.Branch}`: #{opened}, **not regenerated**: {why}.", ct);
        context.Document.Pending.Remove(item);
    }

    private Task ScheduleAsync(string repositoryId, BackportRegeneration item, EBackportRegenerationStep step, TimeSpan? delay, long? runId, CancellationToken ct)
        => outbox.EnqueueAsync(session, new BackportRegenerationStepMessage
        {
            RepositoryId = repositoryId, Head = item.Head, Generation = item.Generation, Step = step, RunId = runId,
        }, delay is null ? null : new BroadcastOptions { Delay = delay }, ct);

    private static string Short(string? sha) => sha is { Length: > 12 } ? sha[..12] : sha ?? "?";

    private sealed class Saga(BackportRegenerationEngine engine, Context context) : IWorkflowHookSaga
    {
        public Repository Repository => context.Repository;

        public IForgeMergeQueue Forge => context.Queue;

        public void Note(string text) => context.Item.Note(engine.Now, text);

        public Task ScheduleAsync(EWorkflowHookStep step, TimeSpan? delay, long? runId, CancellationToken cancellationToken) => step switch
        {
            EWorkflowHookStep.PollRun => engine.ScheduleAsync(context.Repository.Id!, context.Item, EBackportRegenerationStep.PollRun, delay, null, cancellationToken),
            EWorkflowHookStep.Timeout => engine.ScheduleAsync(context.Repository.Id!, context.Item, EBackportRegenerationStep.Timeout, delay, null, cancellationToken),
            EWorkflowHookStep.CancelRun => engine.ScheduleAsync(context.Repository.Id!, context.Item, EBackportRegenerationStep.CancelRun, null, runId, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
        };
    }

    /// <summary>The saga of a cancel step, whose backport may be gone: it only names the repository.</summary>
    private sealed class CancelSaga(Repository repository, IForgeMergeQueue queue) : IWorkflowHookSaga
    {
        public Repository Repository => repository;

        public IForgeMergeQueue Forge => queue;

        public void Note(string text) { }

        public Task ScheduleAsync(EWorkflowHookStep step, TimeSpan? delay, long? runId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
