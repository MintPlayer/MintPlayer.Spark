using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeCoverage.CommitGraph;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.MergeQueue;
using CodeCoverage.WorkflowHooks;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Dependencies.Lifecycle;

/// <summary>The dependency updater's saga (dependency-updates PRD §13.8 N5–N7): what each step and each signal does.</summary>
public interface IDependencyUpdateEngine
{
    Task HandleSignalAsync(DependencyUpdateSignalMessage signal, CancellationToken cancellationToken = default);

    Task HandleStepAsync(DependencyUpdateStepMessage step, CancellationToken cancellationToken = default);
}

/// <summary>
/// The saga, on the merge queue's pattern: every handler loads the repository's <see cref="DependencyUpdateRun"/>, does at
/// most one git-writer job, changes the state and queues its next step through the outbox in the same <c>SaveChanges</c>.
/// </summary>
/// <remarks>
/// <para><b>The run</b> (R3, R4, R14): <c>coverage.yml</c> from the default branch → the lookup dispatched on the default branch
/// → <c>lookup.json</c> → the policy (<see cref="DependencyPolicyEngine"/>) → the plan commit on
/// <see cref="DependencyUpdateRun.WorkBranch"/> → the apply dispatched there → <c>result.json</c> + <c>dependency.patch</c>,
/// judged by the writer's <c>dependencies</c> profile → the R2 holds → the migrations of approved majors (R9) → the rolling
/// branch rebuilt by the writer with the humans' commits replayed (R4) → the pull request upserted → its CI awaited (N6, N7).</para>
/// <para><b>Serial by construction</b>: one lane (<see cref="CoverageQueues.DependencyUpdates"/>), optimistic concurrency, and
/// every step gated on <see cref="DependencyUpdateRun.Run"/> and <see cref="DependencyUpdateRun.Generation"/>.</para>
/// <para><b>Failures</b> as in the merge queue: a transient forge or writer failure re-runs the step with backoff, up to
/// <see cref="MaxTransientRetries"/> times; a refused permission or a writer refusal ends the run with the reason.</para>
/// </remarks>
[Register(typeof(IDependencyUpdateEngine), ServiceLifetime.Scoped)]
public partial class DependencyUpdateEngine : IDependencyUpdateEngine
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IForgeIntegrationResolver forges;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> forgeQueues;
    [Inject] private readonly IEnumerable<IForgeDependencyUpdates> forgeUpdates;
    [Inject] private readonly IEnumerable<IForgeInstallationPermissions> permissionSources;
    [Inject] private readonly IGitWriterSidecar writer;
    [Inject] private readonly IMessageOutbox outbox;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly IOptions<MergeQueueOptions> options;
    [Inject] private readonly IDependencyStateStore stateStore;
    [Inject] private readonly IWorkflowHook hook;
    [Inject] private readonly ILogger<DependencyUpdateEngine> logger;

    public const int MaxTransientRetries = 6;
    public const int MaxRestarts = 3;
    public const int MaxResolvedActions = 50;

    /// <summary>The actor the merge queue's entry names for the updater's own enqueue (R5).</summary>
    public const string QueueActor = "mintplayer-dependencies";

    public const string CommitterName = "CodeCoverage dependency updates";

    internal static readonly TimeSpan LookupTimeout = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan ApplyTimeout = TimeSpan.FromMinutes(45);
    internal static readonly TimeSpan MigrateTimeout = TimeSpan.FromMinutes(45);
    internal static readonly TimeSpan ChecksPollInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan ChecksTimeout = TimeSpan.FromHours(12);

    /// <summary>A red verdict this soon after the bot re-ran the failed checks is the old one: GitHub has not reset them yet.</summary>
    internal static readonly TimeSpan RerunSettle = TimeSpan.FromMinutes(2);

    private const long MaxArtifactBytes = 8 * 1024 * 1024;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private sealed record Context(Repository Repository, Account? Account, IForgeIntegration Forge, IForgeMergeQueue Queue, IForgeDependencyUpdates Updates, DependencyUpdateRun Run);

    // ═══ Signals ═════════════════════════════════════════════════════════════════════════════════════

    public async Task HandleSignalAsync(DependencyUpdateSignalMessage signal, CancellationToken cancellationToken = default)
    {
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        var ct = cancellationToken;
        var create = signal.Kind is EDependencySignal.Trigger or EDependencySignal.Onboard;
        if (await LoadContextAsync(signal.RepositoryId, create, ct) is not { } context) return;
        var run = context.Run;

        switch (signal.Kind)
        {
            case EDependencySignal.Trigger when signal.Trigger is { } trigger:
                await TriggerAsync(context, trigger, signal.SecurityFixes, ct);
                break;

            case EDependencySignal.Onboard:
                await OnboardAsync(context, ct);
                break;

            case EDependencySignal.RunCompleted when signal.RunId is { } runId:
                if (run.State is EDependencyRunState.Looking or EDependencyRunState.Applying or EDependencyRunState.Migrating && run.Pending()?.RunId == runId)
                    await GuardedAsync(context, EDependencyStep.PollRun, () => OnRunAsync(context, fromPoll: false, ct), ct);
                break;

            case EDependencySignal.ChecksChanged when signal.Sha is { } sha:
                if (run.State is EDependencyRunState.AwaitingChecks or EDependencyRunState.Bisecting && string.Equals(run.RollingSha, sha, StringComparison.OrdinalIgnoreCase))
                    await GuardedAsync(context, EDependencyStep.PollChecks, () => EvaluateChecksAsync(context, fromPoll: false, ct), ct);
                break;

            case EDependencySignal.Command when signal.Number is { } number && signal.Command is { } command && !string.IsNullOrEmpty(signal.Actor):
                await CommandAsync(context, number, signal.CommentId, command, signal.Argument, signal.Actor, ct);
                break;

            case EDependencySignal.Merged when signal.Number is { } merged:
                await PullRequestEndedAsync(context, merged, wasMerged: true, ct);
                break;

            case EDependencySignal.Closed when signal.Number is { } closed:
                await PullRequestEndedAsync(context, closed, wasMerged: false, ct);
                break;
        }

        await session.SaveChangesAsync(ct);
    }

    // ═══ Steps ═══════════════════════════════════════════════════════════════════════════════════════

    public async Task HandleStepAsync(DependencyUpdateStepMessage step, CancellationToken cancellationToken = default)
    {
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        var ct = cancellationToken;
        if (await LoadContextAsync(step.RepositoryId, create: false, ct) is not { } context) return;
        var run = context.Run;

        if (step.Step == EDependencyStep.Cleanup)
        {
            await CleanupAsync(context, step.ExpectedSha, ct);
            await session.SaveChangesAsync(ct);
            return;
        }
        if (step.Step == EDependencyStep.CancelRun)
        {
            if (step.RunId is { } runId)
                await hook.CancelAbandonedRunAsync(Saga(context), "dependency", runId, ct);
            await session.SaveChangesAsync(ct);
            return;
        }

        if (run.Run != step.Run || run.Generation != step.Generation || !Expects(step.Step, run.State))
        {
            logger.LogDebug("Dependency updates: ignoring {Step} for {Repository} (run {Run} gen {Generation}; now run {Current} gen {CurrentGeneration}, {State})",
                step.Step, context.Repository.FullName, step.Run, step.Generation, run.Run, run.Generation, run.State);
            return;
        }

        await GuardedAsync(context, step.Step, step.Step switch
        {
            EDependencyStep.Start => () => StartAsync(context, ct),
            EDependencyStep.PollRun => () => OnRunAsync(context, fromPoll: true, ct),
            EDependencyStep.Timeout => () => RunTimeoutAsync(context, ct),
            EDependencyStep.Plan => () => PlanAsync(context, ct),
            EDependencyStep.Publish => () => PublishAsync(context, ct),
            EDependencyStep.PollChecks => () => EvaluateChecksAsync(context, fromPoll: true, ct),
            EDependencyStep.ChecksTimeout => () => ChecksTimedOutAsync(context, ct),
            _ => () => Task.CompletedTask,
        }, ct);

        await session.SaveChangesAsync(ct);
    }

    private static bool Expects(EDependencyStep step, EDependencyRunState state) => step switch
    {
        EDependencyStep.Start => state == EDependencyRunState.Starting,
        EDependencyStep.PollRun or EDependencyStep.Timeout => state is EDependencyRunState.Looking or EDependencyRunState.Applying or EDependencyRunState.Migrating,
        EDependencyStep.Plan => state == EDependencyRunState.Planning,
        EDependencyStep.Publish => state == EDependencyRunState.Publishing,
        EDependencyStep.PollChecks or EDependencyStep.ChecksTimeout => state is EDependencyRunState.AwaitingChecks or EDependencyRunState.Bisecting,
        _ => false,
    };

    // ═══ Triggers ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A run starts at once when none is going (a wait for checks is superseded); otherwise the trigger waits, the strongest
    /// kept. R10: a security trigger interrupts a bisection, which would otherwise block the fix.
    /// </summary>
    private async Task TriggerAsync(Context context, EDependencyRunTrigger trigger, IReadOnlyList<DependencySecurityFixRecord>? fixes, CancellationToken ct)
    {
        var run = context.Run;
        foreach (var fix in fixes ?? [])
            if (!run.SecurityFixes.Any(f => f.Ecosystem == fix.Ecosystem && f.DepName == fix.DepName))
                run.SecurityFixes.Add(fix);

        if (!run.State.IsBusy() || trigger == EDependencyRunTrigger.Security && run.State == EDependencyRunState.Bisecting)
        {
            if (run.State != EDependencyRunState.Idle)
                await hook.AbandonAsync(Saga(context), run.Runs, ct);
            BeginRun(context, trigger);
            await ScheduleAsync(run, EDependencyStep.Start, null, ct);
            return;
        }
        if (run.PendingTrigger is not { } pending || trigger > pending)
            run.PendingTrigger = trigger;
        run.Note(Now, $"{Word(trigger)} trigger waits for run {run.Run} ({run.State})");
    }

    private void BeginRun(Context context, EDependencyRunTrigger trigger)
    {
        var run = context.Run;
        run.Run++;
        run.Trigger = trigger;
        run.PendingTrigger = null;
        // R10's fixes ride with the security run they triggered; any other run plans from the schedule.
        if (trigger != EDependencyRunTrigger.Security)
            run.SecurityFixes = [];
        run.Runs = [];
        run.PlanId = null;
        run.PlanCommitSha = null;
        run.StagedSha = null;
        run.PatchVerdict = null;
        run.AppliedGroups = [];
        run.RequiresWorkflowsPermission = false;
        run.MigrationsPending = [];
        run.MigratedGroups = [];
        run.DroppedGroups = [];
        run.Restarts = 0;
        run.Evaluations = 0;
        run.Bisect = null;
        run.Pins = [];
        ClearRelock(run);
        run.Outcome = null;
        run.OutcomeMessage = null;
        run.StartedAtUtc = Now;
        run.FinishedAtUtc = null;
        Transition(run, EDependencyRunState.Starting, $"run {run.Run} started ({Word(trigger)})");
    }

    // ═══ Start: coverage.yml, the default branch, the lookup dispatch ═══════════════════════════════

    private async Task StartAsync(Context context, CancellationToken ct)
    {
        var (repository, run) = (context.Repository, context.Run);
        if (repository.DefaultBranch is not { } defaultBranch)
        {
            await EndAsync(context, "no-default-branch", "The repository has no default branch yet.", ct);
            return;
        }
        run.DefaultBranch = defaultBranch;

        // O16's rule, here too: the configuration comes from the default branch only (§13.11.3 #13).
        var yml = await context.Forge.GetFileContentAsync(repository, defaultBranch, CoverageYml.FileName, ct);
        var parsed = DependenciesYml.Parse(yml);
        foreach (var error in parsed.Errors.Take(5))
            run.Note(Now, $"coverage.yml: {error}");
        run.FileFrequency = parsed.Section.Schedule?.Frequency;
        run.FileWeekday = parsed.Section.Schedule?.Weekday;
        run.FileHour = parsed.Section.Schedule?.Hour;
        run.FileDisabled = !parsed.Section.Enabled;
        if (!DependencyPolicyConfig.Enabled(parsed.Section, repository, context.Account))
        {
            await EndAsync(context, "disabled", "Dependency updates are off for this repository.", ct);
            return;
        }

        var head = await context.Queue.GetBranchHeadAsync(repository, defaultBranch, ct)
            ?? throw new ForgeRequestException($"The head of {defaultBranch} in {repository.FullName} could not be read.", null);
        run.ExpectedSha = head;
        run.ConfigBlob = await context.Queue.GetFileBlobShaAsync(repository, defaultBranch, CoverageYml.FileName, ct) ?? string.Empty;
        run.LookupAction = parsed.Section.LookupAction;
        run.ApplyAction = parsed.Section.ApplyAction;

        if (!await WorkflowsUsableAsync(context, defaultBranch, head, [run.LookupAction, run.ApplyAction], allowSecrets: true, ct))
            return;
        if (run.Onboarding != EDependencyOnboarding.Done)
        {
            run.Onboarding = EDependencyOnboarding.Done;
            run.Note(Now, "the dependency workflows are on the default branch");
        }

        // D14: dispatched on the default branch, so the run builds its head; a head that moved in between is WrongCommit.
        var token = RunToken(context);
        var request = new WorkflowHookRequest(run.LookupAction, defaultBranch, new Dictionary<string, string>
        {
            ["expected_sha"] = head,
            ["run_id"] = token,
        }, LookupTimeout, "lookup");
        await DispatchAsync(context, request, "lookup", head, $"dependency-lookup-{token}", null, EDependencyRunState.Looking, ct);
    }

    /// <summary>
    /// D12 and R12 before a dispatch: each workflow exists on the default branch, its blob at <paramref name="commit"/> is the
    /// default branch's, and it holds T5's rules. False: the run was ended with the reason.
    /// </summary>
    private async Task<bool> WorkflowsUsableAsync(Context context, string defaultBranch, string commit, IReadOnlyList<string> workflows, bool allowSecrets, CancellationToken ct)
    {
        switch (await hook.VerifyProvenanceAsync(Saga(context), defaultBranch, commit, workflows, ct))
        {
            case { Kind: EWorkflowHookProvenance.Missing, Path: var path }:
                await EndAsync(context, "workflow-missing",
                    $"`{path}` is not on the default branch. {(context.Run.Onboarding == EDependencyOnboarding.PullRequestOpened ? "Merge the onboarding pull request first." : "The onboarding pull request adds it.")}", ct);
                return false;
            case { Kind: EWorkflowHookProvenance.Differs, Path: var path }:
                await EndAsync(context, "workflow-differs", $"`{path}` on the dispatched commit is not the default branch's.", ct);
                return false;
        }
        var files = await context.Queue.GetWorkflowFilesAsync(context.Repository, defaultBranch, ct);
        foreach (var workflow in workflows.Distinct(StringComparer.Ordinal))
        {
            var file = files.FirstOrDefault(f => f.Path == $".github/workflows/{workflow}");
            if (DependencyWorkflowGuard.Refusal(file?.Content, allowSecrets) is { } refusal)
            {
                await EndAsync(context, "workflow-unsafe", $"`{workflow}` is not run by the bot: {refusal} (the bot's workflows read only, with no `id-token`).", ct);
                return false;
            }
        }
        return true;
    }

    private async Task DispatchAsync(Context context, WorkflowHookRequest request, string purpose, string expectedSha, string artifact, string? group,
        EDependencyRunState waiting, CancellationToken ct)
    {
        var run = context.Run;
        var dispatch = await hook.DispatchAsync(Saga(context), request, dispatched =>
        {
            run.Runs.Add(new DependencyWorkflowRun
            {
                Workflow = request.Workflow, RunId = dispatched.RunId, RunUrl = dispatched.RunUrl, Purpose = purpose,
                ExpectedSha = expectedSha, Artifact = artifact, Group = group,
            });
            Transition(run, waiting, $"dispatched {request.Workflow} run {dispatched.RunId} ({purpose}{(group is null ? string.Empty : $" of {group}")})");
            return Task.CompletedTask;
        }, ct);
        switch (dispatch.Outcome)
        {
            case EWorkflowHookDispatch.Refused:
                await EndAsync(context, "dispatch-refused", $"The forge refused to run `{request.Workflow}` ({dispatch.ForgeOutcome}): {dispatch.Message}", ct);
                break;
            case EWorkflowHookDispatch.Uncorrelated:
                await EndAsync(context, "dispatch-uncorrelated", $"The forge started `{request.Workflow}` without saying which run it is, so its result cannot be trusted.", ct);
                break;
        }
    }

    // ═══ Runs ═══════════════════════════════════════════════════════════════════════════════════════

    private async Task OnRunAsync(Context context, bool fromPoll, CancellationToken ct)
    {
        var run = context.Run;
        if (run.Pending() is not { RunId: { } runId } pending) return;
        var observation = await hook.ObserveAsync(Saga(context), pending, pending.ExpectedSha, fromPoll, ct);
        switch (observation.Verdict)
        {
            case EWorkflowHookRunVerdict.Running:
                return;
            case EWorkflowHookRunVerdict.Missing:
                await EndAsync(context, $"{pending.Purpose}-missing", $"The {pending.Purpose} run {runId} no longer exists.", ct);
                return;
            case EWorkflowHookRunVerdict.WrongCommit when pending.Purpose == "lookup":
                await RestartAsync(context, $"the lookup built {Short(observation.Run!.HeadSha)}, not {Short(pending.ExpectedSha)}: the default branch moved", ct);
                return;
            case EWorkflowHookRunVerdict.WrongCommit:
                await EndAsync(context, $"{pending.Purpose}-wrong-commit", $"The {pending.Purpose} run built `{Short(observation.Run!.HeadSha)}`, not `{Short(pending.ExpectedSha)}`.", ct);
                return;
            case EWorkflowHookRunVerdict.Failed when pending.Purpose == "relock":
                await ReplayStoppedAsync(context, GitWriterDependencyOutcomes.ReplayConflict,
                    $"The lockfile regeneration after the replay did not succeed (run {runId}, {observation.Run?.Conclusion ?? "another commit"}).", context.Run.RelockPaths, ct);
                return;
            case EWorkflowHookRunVerdict.Failed when pending.Purpose == "migrate":
                // R9/S17: a failed migration may have left a partial patch; the whole group is dropped.
                await DropMigrationAsync(context, pending, $"its migration run finished {observation.Run!.Conclusion ?? "without a conclusion"}", ct);
                return;
            case EWorkflowHookRunVerdict.Failed:
                await EndAsync(context, $"{pending.Purpose}-failed",
                    $"The {pending.Purpose} run finished `{observation.Run!.Conclusion ?? "without a conclusion"}`{(observation.Run.HtmlUrl is { } url ? $": {url}" : ".")}", ct);
                return;
        }

        switch (pending.Purpose)
        {
            case "lookup":
                await CollectLookupAsync(context, pending, ct);
                break;
            case "apply":
                await CollectApplyAsync(context, pending, ct);
                break;
            case "migrate":
                await CollectMigrateAsync(context, pending, ct);
                break;
            case "relock":
                await CollectRelockAsync(context, pending, ct);
                break;
        }
    }

    private async Task RunTimeoutAsync(Context context, CancellationToken ct)
    {
        var purpose = context.Run.Pending()?.Purpose ?? "workflow";
        await EndAsync(context, $"{purpose}-timeout", $"The {purpose} run did not finish in time.", ct);
    }

    private async Task<IReadOnlyDictionary<string, byte[]>?> ArtifactAsync(Context context, DependencyWorkflowRun run, IReadOnlyList<string> names, CancellationToken ct)
    {
        var files = await context.Updates.DownloadArtifactFilesAsync(context.Repository, run.RunId!.Value, run.Artifact, names, MaxArtifactBytes, ct);
        if (files.Outcome == EForgeArtifactOutcome.Ok && names.All(files.Files.ContainsKey))
            return files.Files;
        await EndAsync(context, $"{run.Purpose}-artifact",
            $"The {run.Purpose} run left an unusable `{run.Artifact}` artifact ({files.Outcome}): {files.Detail ?? $"it lacks {string.Join(" or ", names.Where(n => !files.Files.ContainsKey(n)))}"}", ct);
        return null;
    }

    private async Task CollectLookupAsync(Context context, DependencyWorkflowRun pending, CancellationToken ct)
    {
        var run = context.Run;
        if (await ArtifactAsync(context, pending, ["lookup.json"], ct) is not { } files) return;
        var json = Encoding.UTF8.GetString(files["lookup.json"]);
        var parsed = LookupReportParser.Parse(json, run.ExpectedSha!);
        if (!parsed.IsValid)
        {
            await EndAsync(context, "lookup-invalid", $"lookup.json does not validate: {string.Join("; ", parsed.Errors.Take(5))}", ct);
            return;
        }
        var data = await DataAsync(context, ct);
        data.LookupJson = json;

        // P12: the dependency graph and the fleet view read what the lookup found (every ecosystem it covers, with versions).
        var manifestId = RepositoryManifest.ForRepository(context.Repository.Id!);
        var manifest = await session.LoadAsync<RepositoryManifest>(manifestId, ct);
        if (manifest is null)
        {
            manifest = new RepositoryManifest { Repository = context.Repository.Id, OwnerKey = context.Repository.OwnerKey };
            await session.StoreAsync(manifest, manifestId, ct);
        }
        DependencyLookupIngest.Apply(manifest, parsed.Document!, time.GetUtcNow());

        Transition(run, EDependencyRunState.Planning, $"lookup: {parsed.Document!.Records.Count} records, {parsed.Document.Platforms.Count} platforms");
        await ScheduleAsync(run, EDependencyStep.Plan, null, ct);
    }

    // ═══ Plan: policy, gap C, the plan commit, the apply dispatch ════════════════════════════════════

    private async Task PlanAsync(Context context, CancellationToken ct)
    {
        var (repository, run) = (context.Repository, context.Run);
        var data = await DataAsync(context, ct);
        var lookup = DependencyContract.Deserialize<LookupDocument>(data.LookupJson!)!;
        var section = DependenciesYml.Parse(await context.Forge.GetFileContentAsync(repository, run.ExpectedSha!, CoverageYml.FileName, ct)).Section;
        var config = DependencyPolicyConfig.Resolve(section, repository, context.Account);
        var state = await stateStore.LoadAsync(repository.Id!, ct);
        var fleet = context.Account?.Id is { } accountId ? await stateStore.LoadFleetAsync(accountId, ct) : new DependencyFleetState();

        // Gap E: an approval counts only while its approver still has write access, read now, at apply time.
        foreach (var approval in state.Approvals.ToList())
        {
            if (await context.Queue.GetActorRoleAsync(repository, approval.ApprovedBy, ct) < EForgeRepositoryRole.Write)
            {
                state.Approvals.Remove(approval);
                run.Note(Now, $"the approval of {approval.Group} by @{approval.ApprovedBy} lapsed: no write access any more");
            }
        }

        // §7.6, gap E: releases confirmed as the account's own skip the cooldown.
        if (context.Account?.Id is not null)
            await ConfirmOwnReleasesAsync(context, lookup, fleet, ct);

        var outcome = DependencyPolicyEngine.Evaluate(new DependencyPolicyInput
        {
            Lookup = lookup,
            Config = config,
            State = state,
            Fleet = fleet,
            Now = time.GetUtcNow(),
            ConfigBlob = run.ConfigBlob,
            IsScheduledSlot = run.Trigger == EDependencyRunTrigger.Scheduled && run.Evaluations == 0,
            Mode = run.Trigger == EDependencyRunTrigger.Security ? EDependencyRunMode.Security : EDependencyRunMode.Scheduled,
            SecurityFixes = [.. run.SecurityFixes.Select(f => new DependencySecurityFix(f.Ecosystem, f.DepName, f.Advisory))],
        });
        run.Evaluations++;
        Apply(state, outcome.NextState);

        // D-N1: a human fix on the rolling branch keeps the versions it was written against.
        var (pinnedOutcome, pins) = await FixPinsAsync(context, data, lookup, state, outcome, ct);
        outcome = pinnedOutcome;
        run.Pins = [.. pins.Select(p => $"{p.DepName} {p.Version} ({p.PackageFile})")];

        var excluded = run.DroppedGroups.Concat(run.Bisect?.Excluded ?? []).ToHashSet(StringComparer.Ordinal);
        var groups = outcome.Groups.Where(g => !excluded.Contains(g.Group)).ToList();

        // Gap C: the bot pins a SHA-pinned Actions bump to the commit IT resolved for the tag, and reads the target
        // action.yml statically for R15's interface check.
        var failures = new List<DependencyReportFailure>();
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in groups.ToList())
        {
            foreach (var item in group.Items.Where(IsAction).Take(MaxResolvedActions))
            {
                var (ownerRepo, path) = SplitAction(item.Record.DepName);
                var tag = item.Candidate.Value ?? item.Candidate.Version;
                if (ownerRepo is null || tag is null) continue;
                string? sha = null;
                if (item.Record.CurrentDigest is not null)
                {
                    sha = await context.Updates.ResolveTagCommitAsync(repository, ownerRepo, tag, ct);
                    if (sha is null)
                    {
                        groups.Remove(group);
                        failures.Add(new DependencyReportFailure(group.Group, "not applied", $"the tag {ownerRepo}@{tag} could not be resolved to a commit", false));
                        break;
                    }
                    resolved[$"{item.Record.DepName}@{tag}"] = sha;
                }
                var newRef = sha ?? tag;
                var actionYml = await context.Updates.GetRepositoryFileAsync(repository, ownerRepo, newRef, path is null ? "action.yml" : $"{path}/action.yml", ct)
                    ?? await context.Updates.GetRepositoryFileAsync(repository, ownerRepo, newRef, path is null ? "action.yaml" : $"{path}/action.yaml", ct);
                if (actionYml is not null)
                    targets[$"{item.Record.DepName}@{newRef}"] = actionYml;
            }
        }
        data.ResolvedActions = resolved;
        data.TargetActions = targets;

        var report = DependencyRunReport.From(outcome with { Groups = groups }, []) with
        {
            Failures = failures,
            Pins = [.. pins.Where(p => groups.Any(g => g.Group == p.Group))],
        };
        data.ReportJson = JsonSerializer.Serialize(report, JsonSerializerOptions.Web);

        if (groups.Count == 0)
        {
            if (run.PullRequestNumber is not null)
                await UpdatePullRequestBodyAsync(context, report, null, ct);
            await EndAsync(context, "nothing-planned", "Nothing to apply this time.", ct);
            return;
        }

        var planId = $"{repository.GitHubId.ToString(CultureInfo.InvariantCulture)}-{run.Run.ToString(CultureInfo.InvariantCulture)}-{run.Evaluations.ToString(CultureInfo.InvariantCulture)}";
        var plan = (outcome with { Groups = groups }).ToPlan(planId, run.ExpectedSha!);
        plan = plan with
        {
            Groups = [.. plan.Groups.Select(g => g with
            {
                Items = [.. g.Items.Select(i => resolved.TryGetValue($"{i.DepName}@{i.NewValue}", out var sha) ? i with { NewDigest = sha } : i)],
            })],
        };
        var planJson = DependencyContractReader.WritePlan(plan);
        data.PlanJson = planJson;
        run.PlanId = planId;

        // R14: the plan travels as one commit on the bot's dispatch ref; nothing but the plan file changes.
        if (await WriterAsync(context, false, (access, credential) => writer.DependencyWorkRefAsync(Request(context, access, credential) with
            {
                Author = Identity(run), Committer = Identity(run), Message = $"Dependency plan {planId}", PlanJson = planJson, ExpectedWorkSha = run.WorkRefSha,
            }, ct), ct) is not { } result)
            return;
        switch (result.Outcome)
        {
            case GitWriterOutcomes.Pushed when result.Sha is { } sha:
                run.PlanCommitSha = sha;
                run.WorkRefSha = sha;
                break;
            case GitWriterDependencyOutcomes.BranchMoved:
                run.WorkRefSha = null;
                throw new GitWriterUnavailableException($"{DependencyUpdateRun.WorkBranch} moved under the plan commit: {result.Detail}");
            case GitWriterOutcomes.Busy or GitWriterOutcomes.Failed:
                throw new GitWriterUnavailableException($"The git writer answered the plan commit with {result.Outcome}: {result.Detail}");
            default:
                await EndAsync(context, "plan-commit", $"The plan commit was refused ({result.Outcome}): {result.Detail}", ct);
                return;
        }

        if (!await WorkflowsUsableAsync(context, run.DefaultBranch!, run.PlanCommitSha!, [run.ApplyAction], allowSecrets: true, ct))
            return;
        var token = RunToken(context);
        var request = new WorkflowHookRequest(run.ApplyAction, DependencyUpdateRun.WorkBranch, new Dictionary<string, string>
        {
            ["expected_sha"] = run.ExpectedSha!,
            ["run_id"] = token,
        }, ApplyTimeout, "apply");
        await DispatchAsync(context, request, "apply", run.PlanCommitSha!, $"dependency-apply-{token}", null, EDependencyRunState.Applying, ct);
    }

    /// <summary>
    /// D-N1: when people pushed commits above the bot's own on the rolling branch, and those commits change more than the
    /// previous plan's manifests and lockfiles, the groups of that plan stay at the versions the fix was made against.
    /// </summary>
    private async Task<(DependencyPolicyOutcome Outcome, IReadOnlyList<DependencyPin> Pins)> FixPinsAsync(Context context, DependencyUpdateRunData data,
        LookupDocument lookup, DependencyUpdateState state, DependencyPolicyOutcome outcome, CancellationToken ct)
    {
        var (repository, run) = (context.Repository, context.Run);
        if (run.PullRequestNumber is null || run.RollingBotSha is not { } bot || data.RollingPlanJson is not { } previousJson
            || DependencyContract.Deserialize<DependencyPlan>(previousJson) is not { } previous)
            return (outcome, []);
        var head = await context.Queue.GetBranchHeadAsync(repository, DependencyUpdateRun.RollingBranch, ct);
        if (head is null || string.Equals(head, bot, StringComparison.OrdinalIgnoreCase))
            return (outcome, []);
        var comparison = await context.Forge.CompareAsync(repository, bot, head, ct);
        IReadOnlyCollection<string>? changed = comparison is null || comparison.Truncated
            ? null
            : [.. comparison.Files.SelectMany(f => f.PreviousPath is { } previousPath ? new[] { f.Path, previousPath } : [f.Path])];
        if (!DependencyFixPins.HumanFixPresent(previous, changed))
            return (outcome, []);
        var pinned = DependencyFixPins.Apply(outcome, previous, lookup, state);
        foreach (var pin in pinned.Pins)
            run.Note(Now, $"{pin.DepName} stays at {pin.Version}: a fix on {DependencyUpdateRun.RollingBranch} was made against it (D-N1)");
        return pinned;
    }

    private static bool IsAction(DependencyPlannedItem item) => item.Record.Ecosystem == "github-actions" || item.Record.Manager == "github-actions";

    /// <summary><c>owner/repo[/path]</c> → (<c>owner/repo</c>, path or null); null for anything else.</summary>
    internal static (string? OwnerRepo, string? Path) SplitAction(string depName)
    {
        var parts = depName.Split('/');
        if (parts.Length < 2 || parts.Any(p => p.Length == 0 || p is "." or ".."))
            return (null, null);
        return ($"{parts[0]}/{parts[1]}", parts.Length > 2 ? string.Join('/', parts[2..]) : null);
    }

    private async Task ConfirmOwnReleasesAsync(Context context, LookupDocument lookup, DependencyFleetState fleet, CancellationToken ct)
    {
        var producers = fleet.ProducerEvents.Select(e => e.ProducerRepository).Distinct(StringComparer.Ordinal).ToList();
        if (producers.Count == 0) return;
        var manifests = await session.LoadAsync<RepositoryManifest>(producers.Select(RepositoryManifest.ForRepository), ct);
        var confirmed = OwnReleaseCheck.Confirm(lookup, [.. manifests.Values.OfType<RepositoryManifest>()], fleet.ProducerEvents, time.GetUtcNow());
        foreach (var release in confirmed)
        {
            if (fleet.OwnReleases.Any(r => r.Ecosystem == release.Ecosystem && r.DepName == release.DepName && r.Version == release.Version)) continue;
            fleet.OwnReleases.Add(release);
            context.Run.Note(Now, $"{release.DepName} {release.Version} is the account's own release (published by {release.ProducerRepository}): no cooldown");
        }
        if (fleet.OwnReleases.Count > DependencyFleetState.MaxEntries)
            fleet.OwnReleases = [.. fleet.OwnReleases.OrderByDescending(r => r.AtUtc).Take(DependencyFleetState.MaxEntries)];
    }

    private static void Apply(DependencyUpdateState state, DependencyUpdateState next)
    {
        state.FirstSeen = next.FirstSeen;
        state.Holds = next.Holds;
        state.Approvals = next.Approvals;
        state.Slots = next.Slots;
        state.LastEvaluatedAtUtc = next.LastEvaluatedAtUtc;
    }

    // ═══ Apply: R3, the patch verdict, R2 holds, R9 migrations ══════════════════════════════════════

    private async Task CollectApplyAsync(Context context, DependencyWorkflowRun pending, CancellationToken ct)
    {
        var (repository, run) = (context.Repository, context.Run);
        var data = await DataAsync(context, ct);
        var lookup = DependencyContract.Deserialize<LookupDocument>(data.LookupJson!)!;
        var plan = DependencyContract.Deserialize<DependencyPlan>(data.PlanJson!)!;

        // R3: a base that moved is fine unless it touched what the plan edits or reads.
        var head = await context.Queue.GetBranchHeadAsync(repository, run.DefaultBranch!, ct);
        if (head is not null && !string.Equals(head, run.ExpectedSha, StringComparison.OrdinalIgnoreCase))
        {
            var comparison = await context.Forge.CompareAsync(repository, run.ExpectedSha!, head, ct);
            var watched = lookup.Records.SelectMany(r => r.LockFiles.Prepend(r.PackageFile))
                .Concat(plan.Groups.SelectMany(g => g.Items).SelectMany(i => i.LockFiles.Prepend(i.PackageFile)))
                .ToHashSet(StringComparer.Ordinal);
            if (comparison is null || comparison.Truncated || comparison.Files.Any(f => watched.Contains(f.Path) || f.PreviousPath is { } p && watched.Contains(p)))
            {
                await RestartAsync(context, $"{run.DefaultBranch} moved to {Short(head)} and changed a manifest or lockfile the plan reads", ct);
                return;
            }
            run.Note(Now, $"{run.DefaultBranch} moved to {Short(head)}, but no manifest or lockfile changed: the plan stands (R3)");
        }

        if (await ArtifactAsync(context, pending, ["result.json", "dependency.patch"], ct) is not { } files) return;
        var resultRead = DependencyContractReader.ReadApplyResult(Encoding.UTF8.GetString(files["result.json"]));
        if (!resultRead.IsValid || resultRead.Document!.PlanId != run.PlanId || resultRead.Document.ExpectedSha != run.ExpectedSha)
        {
            await EndAsync(context, "result-invalid", resultRead.IsValid
                ? "result.json names another plan or another commit."
                : $"result.json does not validate: {string.Join("; ", resultRead.Errors.Take(5))}", ct);
            return;
        }
        var result = resultRead.Document;
        var patch = files["dependency.patch"];
        var claimed = result.Groups.Where(g => g.Status == EApplyGroupStatus.Applied).Select(g => g.Group).ToList();
        var section = DependenciesYml.Parse(await context.Forge.GetFileContentAsync(repository, run.ExpectedSha!, CoverageYml.FileName, ct)).Section;

        // The runner installed fresh packages: its patch is judged by the writer before anything is built from it.
        if (await WriterAsync(context, false, (access, credential) => writer.DependencyValidateAsync(Request(context, access, credential) with
            {
                Plan = data.PlanJson, Patch = Convert.ToBase64String(patch), ClaimedGroups = claimed, TargetActions = data.TargetActions, AllowedHosts = section.Registries,
            }, ct), ct) is not { } verdict)
            return;
        if (verdict.Outcome is GitWriterOutcomes.Busy or GitWriterOutcomes.Failed)
            throw new GitWriterUnavailableException($"The git writer answered the validation with {verdict.Outcome}: {verdict.Detail}");
        if (verdict.Verdict is not (GitWriterDependencyOutcomes.Accepted or GitWriterDependencyOutcomes.NeedsHuman))
        {
            var why = $"The apply patch was refused ({verdict.Reason}): {verdict.Detail}{(verdict.Paths.Count > 0 ? $" ({string.Join(", ", verdict.Paths.Take(5))})" : string.Empty)}";
            await CommentAsync(context, $"⚠️ {why} Nothing was pushed.", ct);
            await EndAsync(context, "patch-rejected", why, ct);
            return;
        }

        var state = await stateStore.LoadAsync(repository.Id!, ct);
        foreach (var hold in DependencyHolds.RecordApplyResult(state, plan, result, lookup, time.GetUtcNow()))
            run.Note(Now, $"held {hold.Group}: {hold.Reason}");

        data.Patch = Convert.ToBase64String(patch);
        data.ApplyResultJson = JsonSerializer.Serialize(result, DependencyContract.JsonOptions);
        run.AppliedGroups = [.. verdict.AppliedGroups];
        run.RequiresWorkflowsPermission = verdict.RequiresWorkflowsPermission;
        run.PatchVerdict = verdict.Verdict;

        var report = Report(data);
        report = report with
        {
            Groups = [.. report.Groups.Where(g => run.AppliedGroups.Contains(g.Group))],
            Failures = [.. report.Failures, .. result.Groups.Where(g => g.Status != EApplyGroupStatus.Applied).Select(g => new DependencyReportFailure(g.Group,
                g.Status == EApplyGroupStatus.Unresolvable ? "the resolver refused it" : "the apply failed", g.Blockers.FirstOrDefault()?.Message ?? g.Message,
                (g.Message ?? string.Empty).Contains("no adapter", StringComparison.OrdinalIgnoreCase) || g.Blockers.Any(b => b.Code == "no-adapter")))],
            NeedsHumanDetail = verdict.Verdict == GitWriterDependencyOutcomes.NeedsHuman
                ? $"The lockfiles bring in {verdict.NewPackages.Count} new packages, more than the bot merges without a person (gap B)." : null,
        };
        data.ReportJson = JsonSerializer.Serialize(report, JsonSerializerOptions.Web);

        if (report.Groups.Count == 0)
        {
            if (run.PullRequestNumber is not null)
                await UpdatePullRequestBodyAsync(context, report, null, ct);
            await EndAsync(context, "nothing-applied", "The apply run applied nothing.", ct);
            return;
        }

        // R9: approved majors with a migrate rule run their migration on the applied commit, one after the other.
        run.MigrationsPending = [.. report.Groups.Where(g => g.Approved && g.MigrateWorkflow is not null).Select(g => g.Group)];
        if (run.MigrationsPending.Count == 0)
        {
            Transition(run, EDependencyRunState.Publishing, $"the apply patch is {verdict.Verdict}: {string.Join(", ", run.AppliedGroups)}");
            await ScheduleAsync(run, EDependencyStep.Publish, null, ct);
            return;
        }

        if (await WriterAsync(context, false, (access, credential) => writer.DependencyWorkRefAsync(Request(context, access, credential) with
            {
                Author = Identity(run), Committer = Identity(run), Message = $"Dependency plan {run.PlanId}, applied", Plan = data.PlanJson, Patch = data.Patch,
                ClaimedGroups = claimed, TargetActions = data.TargetActions, AllowedHosts = section.Registries, ExpectedWorkSha = run.WorkRefSha,
            }, ct), ct) is not { } staged)
            return;
        if (staged.Outcome != GitWriterOutcomes.Pushed || staged.Sha is null)
        {
            if (staged.Outcome is GitWriterOutcomes.Busy or GitWriterOutcomes.Failed or GitWriterDependencyOutcomes.BranchMoved)
                throw new GitWriterUnavailableException($"The git writer answered the staged commit with {staged.Outcome}: {staged.Detail}");
            await EndAsync(context, "stage-refused", $"The applied commit for the migrations was refused ({staged.Outcome}): {staged.Detail}", ct);
            return;
        }
        run.StagedSha = staged.Sha;
        run.WorkRefSha = staged.Sha;
        await DispatchMigrationAsync(context, report, ct);
    }

    private async Task DispatchMigrationAsync(Context context, DependencyRunReport report, CancellationToken ct)
    {
        var run = context.Run;
        var group = run.MigrationsPending[0];
        var planned = report.Groups.First(g => g.Group == group);
        var workflow = planned.MigrateWorkflow!;
        if (!await WorkflowsUsableAsync(context, run.DefaultBranch!, run.StagedSha!, [workflow], allowSecrets: false, ct))
            return;
        var packages = JsonSerializer.Serialize(planned.Items.Select(i => new { name = i.DepName, from = i.From, to = i.NewVersion }).DistinctBy(p => p.name));
        var token = RunToken(context);
        var request = new WorkflowHookRequest(workflow, DependencyUpdateRun.WorkBranch, new Dictionary<string, string>
        {
            ["base_sha"] = run.StagedSha!,
            ["packages"] = packages,
            ["run_id"] = token,
        }, MigrateTimeout, "migrate");
        await DispatchAsync(context, request, "migrate", run.StagedSha!, $"dependency-migrate-{token}", group, EDependencyRunState.Migrating, ct);
    }

    private async Task CollectMigrateAsync(Context context, DependencyWorkflowRun pending, CancellationToken ct)
    {
        var (repository, run) = (context.Repository, context.Run);
        var files = await context.Updates.DownloadArtifactFilesAsync(repository, pending.RunId!.Value, pending.Artifact, ["migrate-result.json", "migrate.patch"], MaxArtifactBytes, ct);
        // S17: conclusion success AND the completion marker, else the migration may be partial and the group is dropped.
        if (files.Outcome != EForgeArtifactOutcome.Ok || !files.Files.TryGetValue("migrate-result.json", out var marker) || !files.Files.TryGetValue("migrate.patch", out var patch))
        {
            await DropMigrationAsync(context, pending, "its run left no completion marker", ct);
            return;
        }
        var read = DependencyContractReader.ReadMigrateResult(Encoding.UTF8.GetString(marker));
        if (!read.IsValid || !read.Document!.Completed || read.Document.BaseSha != run.StagedSha)
        {
            await DropMigrationAsync(context, pending, read.IsValid ? "its marker is not complete or names another commit" : "its marker does not validate", ct);
            return;
        }

        var data = await DataAsync(context, ct);
        var lookupless = DependenciesYml.Parse(await context.Forge.GetFileContentAsync(repository, run.ExpectedSha!, CoverageYml.FileName, ct)).Section;
        var config = DependencyPolicyConfig.Resolve(lookupless, repository, context.Account);
        var state = await stateStore.LoadAsync(repository.Id!, ct);
        var fleet = context.Account?.Id is { } accountId ? await stateStore.LoadFleetAsync(accountId, ct) : new DependencyFleetState();
        var notes = DependencyMigrationNotes.Check(read.Document, config, state, fleet);
        data.Migrations.Add(new DependencyMigrationRecord
        {
            Group = pending.Group!, Workflow = pending.Workflow, Patch = Convert.ToBase64String(patch),
            Notes = [.. notes.Select(n => $"{n.DepName} {n.Version} ({n.PackageFile}): {n.Reason}")],
            ExtraBumps = [.. read.Document.ExtraBumps.Select(b => $"{b.DepName} {b.From} → {b.To} ({b.PackageFile})")],
        });
        var report = Report(data);
        data.ReportJson = JsonSerializer.Serialize(report with
        {
            MigrationNotes = [.. report.MigrationNotes, .. notes.Select(n => $"Held version introduced by migration: {n.DepName} {n.Version} — {n.Reason}"),
                .. read.Document.ExtraBumps.Select(b => $"The {pending.Group} migration also bumped {b.DepName} {b.From} → {b.To} (R19).")],
        }, JsonSerializerOptions.Web);
        run.MigrationsPending.Remove(pending.Group!);
        run.MigratedGroups.Add(pending.Group!);

        if (run.MigrationsPending.Count > 0)
        {
            await DispatchMigrationAsync(context, Report(data), ct);
            return;
        }
        Transition(run, EDependencyRunState.Publishing, $"migrated {string.Join(", ", run.MigratedGroups)}");
        await ScheduleAsync(run, EDependencyStep.Publish, null, ct);
    }

    /// <summary>R9: a migration that did not complete drops its whole group: held, and the plan applied again without it.</summary>
    private async Task DropMigrationAsync(Context context, DependencyWorkflowRun pending, string why, CancellationToken ct)
    {
        var (repository, run) = (context.Repository, context.Run);
        var group = pending.Group!;
        var data = await DataAsync(context, ct);
        var lookup = DependencyContract.Deserialize<LookupDocument>(data.LookupJson!)!;
        var state = await stateStore.LoadAsync(repository.Id!, ct);
        state.Holds.RemoveAll(h => h.Id == $"migrate:{group}");
        state.Holds.Add(new DependencyHold
        {
            Id = $"migrate:{group}", Kind = EDependencyHoldKind.Unattributed, Group = group,
            DepNames = [.. Report(data).Groups.FirstOrDefault(g => g.Group == group)?.Items.Select(i => i.DepName).Distinct(StringComparer.Ordinal) ?? []],
            Reason = $"its migration did not complete ({why}); retried when the lookup changes (R9)",
            Fingerprint = DependencyHolds.Fingerprint(lookup), CreatedAtUtc = Now, PlanId = run.PlanId,
        });
        run.DroppedGroups.Add(group);
        run.MigrationsPending.Clear();
        run.MigratedGroups.Clear();
        data.Migrations.Clear();
        Transition(run, EDependencyRunState.Planning, $"dropped {group}: {why}; planning again without it");
        await ScheduleAsync(run, EDependencyStep.Plan, null, ct);
    }

    // ═══ Publish: the rolling branch (R4) and the pull request (R16) ═════════════════════════════════

    private async Task PublishAsync(Context context, CancellationToken ct)
    {
        var (repository, run) = (context.Repository, context.Run);
        var data = await DataAsync(context, ct);
        var lookup = DependencyContract.Deserialize<LookupDocument>(data.LookupJson!)!;
        var report = Report(data);
        var section = DependenciesYml.Parse(await context.Forge.GetFileContentAsync(repository, run.ExpectedSha!, CoverageYml.FileName, ct)).Section;
        var claimed = DependencyContract.Deserialize<ApplyResult>(data.ApplyResultJson!)!.Groups
            .Where(g => g.Status == EApplyGroupStatus.Applied).Select(g => g.Group).ToList();
        var rollingHead = await context.Queue.GetBranchHeadAsync(repository, DependencyUpdateRun.RollingBranch, ct);

        if (await WriterAsync(context, run.RequiresWorkflowsPermission, (access, credential) => writer.DependencyRollingAsync(Request(context, access, credential) with
            {
                Author = Identity(run), Committer = Identity(run), Message = CommitMessage(report),
                Plan = data.PlanJson, Patch = data.Patch, ClaimedGroups = claimed, TargetActions = data.TargetActions, AllowedHosts = section.Registries,
                Migrations = [.. data.Migrations.Select(m => new GitWriterMigratePatch(m.Group, m.Patch))],
                ExpectedRollingSha = rollingHead, LastBotSha = rollingHead is null ? null : run.RollingBotSha, ExpectedWorkSha = run.WorkRefSha,
            }, ct), ct) is not { } result)
            return;

        run.RelockRollingSha = rollingHead;
        await AfterRollingAsync(context, data, report, lookup, result, botSha: null, ct);
    }

    /// <summary>What a rolling build answered: the human holds, then the push (or the relock, D-N2, or the stop), then the pull
    /// request, the bot's check and the wait for CI. <paramref name="botSha"/> overrides the answer's bot commit (a relock's
    /// answer names the relock commit; the replay must start from the dependency commit under it).</summary>
    private async Task AfterRollingAsync(Context context, DependencyUpdateRunData data, DependencyRunReport report, LookupDocument lookup,
        GitWriterDependencyResult result, string? botSha, CancellationToken ct)
    {
        var (repository, run) = (context.Repository, context.Run);
        var state = await stateStore.LoadAsync(repository.Id!, ct);
        var newHolds = 0;
        foreach (var value in result.HumanValues)
        {
            if (!state.Holds.Any(h => h.Kind == EDependencyHoldKind.Human && h.DepNames.Contains(value.DepName) && h.HeldValue == value.HeldValue))
                newHolds++;
            DependencyHolds.AddHumanHold(state, value.DepName, value.HeldValue, value.ProposedValue, value.Author ?? "someone", value.Commit, lookup, time.GetUtcNow());
            run.Note(Now, $"{value.DepName} is held at {value.HeldValue}: set by hand in {Short(value.Commit)} (Q11)");
        }

        switch (result.Outcome)
        {
            case GitWriterOutcomes.Pushed when result.Sha is { } pushed:
                run.RollingSha = pushed;
                run.RollingBotSha = botSha ?? result.BotSha;
                run.Note(Now, $"pushed {DependencyUpdateRun.RollingBranch} at {Short(pushed)}{(result.Replayed > 0 ? $" with {result.Replayed} human commit(s) replayed" : string.Empty)}");
                break;
            case GitWriterDependencyOutcomes.Unchanged when result.Sha is { } unchanged:
                run.RollingSha = unchanged;
                run.RollingBotSha = botSha ?? run.RollingBotSha ?? result.BotSha;
                run.Note(Now, "the rebuilt branch equals the pushed one: nothing pushed, no CI (R4)");
                break;
            case GitWriterDependencyOutcomes.RelockNeeded when result.Sha is { } replayed && botSha is null:
                await StartRelockAsync(context, data, result, replayed, ct);
                return;
            case GitWriterDependencyOutcomes.ReplayConflict when newHolds > 0 && !run.ReplayRetried:
                // D-N2: the person's values are holds now, so the next plan leaves those dependencies alone and the same
                // commits replay without the conflict. Retried once, at once; a second conflict stops.
                run.ReplayRetried = true;
                if (run.PendingTrigger is not { } waiting || EDependencyRunTrigger.Refresh > waiting)
                    run.PendingTrigger = EDependencyRunTrigger.Refresh;
                await EndAsync(context, "replay-retry", $"{result.Detail} The values set by hand are holds now; rebuilding once without them.", ct);
                return;
            case GitWriterDependencyOutcomes.ReplayConflict:
            case GitWriterDependencyOutcomes.RollingRewritten:
                await ReplayStoppedAsync(context, result.Outcome, result.Detail ?? result.Outcome, result.Paths, ct);
                return;
            case GitWriterDependencyOutcomes.BranchMoved when botSha is not null:
                // A person pushed while the lockfiles were regenerated: rebuild from the apply patch, with the new commits too.
                ClearRelock(run);
                Transition(run, EDependencyRunState.Publishing, $"{DependencyUpdateRun.RollingBranch} moved during the relock; rebuilding it");
                await ScheduleAsync(run, EDependencyStep.Publish, null, ct);
                return;
            case GitWriterOutcomes.Busy or GitWriterOutcomes.Failed or GitWriterDependencyOutcomes.BranchMoved:
                throw new GitWriterUnavailableException($"The git writer answered the rolling build with {result.Outcome}: {result.Detail}");
            default:
                await EndAsync(context, result.Outcome == GitWriterOutcomes.PatchRejected ? "patch-rejected" : "rolling-refused",
                    $"The rolling branch was not built ({result.Outcome}, {result.Reason}): {result.Detail}", ct);
                return;
        }
        // D-N1: the plan this bot commit was built from is the base any later human fix is made against.
        data.RollingPlanJson = data.PlanJson;
        run.ReplayRetried = false;
        ClearRelock(run);

        var bisecting = run.Bisect is { Culprit: null };
        await UpsertPullRequestAsync(context, report, null, ct);
        // Old §7.4: while a bisection runs, the bot's own check stays in progress on the head, a "don't merge".
        await PublishOwnCheckAsync(context, run.RollingSha!, bisecting ? EForgeQueueCheckState.InProgress : EForgeQueueCheckState.Neutral,
            bisecting ? "Bisecting: don't merge" : "Waiting for CI",
            bisecting ? $"Testing without {string.Join(", ", run.Bisect!.Excluded)} (step {run.Bisect.Spent} of {run.Bisect.Budget})." : $"{report.Groups.Count} group(s) applied; waiting for this commit's checks.", ct);
        await ScheduleCleanupAsync(context, ct);

        if (run.PendingTrigger is { } next)
        {
            await EndAsync(context, "published", $"published {Short(run.RollingSha)}; a {Word(next)} trigger is waiting", ct);
            return;
        }
        Transition(run, bisecting ? EDependencyRunState.Bisecting : EDependencyRunState.AwaitingChecks, $"waiting for the checks of {Short(run.RollingSha)}");
        await ScheduleAsync(run, EDependencyStep.PollChecks, ChecksPollInterval, ct);
        await ScheduleAsync(run, EDependencyStep.ChecksTimeout, ChecksTimeout, ct);
    }

    // ═══ Relock (D-N2): lockfile conflicts in the replay, regenerated by the apply workflow ════════════

    /// <summary>
    /// The writer took the bot's side of the conflicting lockfiles and left the replayed head on the work ref. A relock plan
    /// (the plan's items that own those lockfiles, as lockfile-only updates at the versions already in the manifests) goes on
    /// top of it, and the repository's own apply workflow regenerates the lockfiles from the manifests, scripts off, exactly
    /// like an apply. Its patch is judged by the same <c>dependencies</c> profile before the writer commits it.
    /// </summary>
    private async Task StartRelockAsync(Context context, DependencyUpdateRunData data, GitWriterDependencyResult result, string replayed, CancellationToken ct)
    {
        var run = context.Run;
        run.WorkRefSha = replayed;
        run.RelockBaseSha = replayed;
        run.RelockBotSha = result.BotSha;
        run.RelockPaths = [.. result.Paths];
        var plan = DependencyContract.Deserialize<DependencyPlan>(data.PlanJson!)!;
        var byHand = result.HumanValues.Select(v => (v.DepName, v.PackageFile)).ToHashSet();
        if (DependencyRelockPlan.Build(plan, result.Paths, byHand, $"{run.PlanId}-relock", replayed) is not { } relock)
        {
            await ReplayStoppedAsync(context, GitWriterDependencyOutcomes.ReplayConflict,
                "A replayed commit conflicts in a lockfile whose planned dependencies were all changed by hand, so the bot cannot regenerate it.", result.Paths, ct);
            return;
        }
        var json = DependencyContractReader.WritePlan(relock);
        data.RelockPlanJson = json;
        run.Note(Now, $"replayed {result.Replayed} human commit(s); regenerating {string.Join(", ", result.Paths)} (D-N2)");

        if (await WriterAsync(context, false, (access, credential) => writer.DependencyWorkRefAsync(Request(context, access, credential) with
            {
                ExpectedSha = replayed, Author = Identity(run), Committer = Identity(run), Message = $"Dependency relock plan {relock.PlanId}",
                PlanJson = json, ExpectedWorkSha = replayed,
            }, ct), ct) is not { } committed)
            return;
        switch (committed.Outcome)
        {
            case GitWriterOutcomes.Pushed when committed.Sha is { } sha:
                run.WorkRefSha = sha;
                break;
            case GitWriterOutcomes.Busy or GitWriterOutcomes.Failed or GitWriterDependencyOutcomes.BranchMoved:
                throw new GitWriterUnavailableException($"The git writer answered the relock plan commit with {committed.Outcome}: {committed.Detail}");
            default:
                await EndAsync(context, "plan-commit", $"The relock plan commit was refused ({committed.Outcome}): {committed.Detail}", ct);
                return;
        }

        if (!await WorkflowsUsableAsync(context, run.DefaultBranch!, run.WorkRefSha!, [run.ApplyAction], allowSecrets: true, ct))
            return;
        var token = RunToken(context);
        var request = new WorkflowHookRequest(run.ApplyAction, DependencyUpdateRun.WorkBranch, new Dictionary<string, string>
        {
            ["expected_sha"] = replayed,
            ["run_id"] = token,
        }, ApplyTimeout, "relock");
        await DispatchAsync(context, request, "relock", run.WorkRefSha!, $"dependency-apply-{token}", null, EDependencyRunState.Applying, ct);
    }

    private async Task CollectRelockAsync(Context context, DependencyWorkflowRun pending, CancellationToken ct)
    {
        var (repository, run) = (context.Repository, context.Run);
        var data = await DataAsync(context, ct);
        if (await ArtifactAsync(context, pending, ["result.json", "dependency.patch"], ct) is not { } files) return;
        var relock = DependencyContract.Deserialize<DependencyPlan>(data.RelockPlanJson!)!;
        var read = DependencyContractReader.ReadApplyResult(Encoding.UTF8.GetString(files["result.json"]));
        if (!read.IsValid || read.Document!.PlanId != relock.PlanId || read.Document.ExpectedSha != run.RelockBaseSha)
        {
            await ReplayStoppedAsync(context, GitWriterDependencyOutcomes.ReplayConflict, "The lockfile regeneration left a result.json that does not validate or names another plan.", run.RelockPaths, ct);
            return;
        }
        var claimed = read.Document.Groups.Where(g => g.Status == EApplyGroupStatus.Applied).Select(g => g.Group).ToList();
        if (read.Document.Groups.FirstOrDefault(g => g.Status != EApplyGroupStatus.Applied) is { } failed)
        {
            await ReplayStoppedAsync(context, GitWriterDependencyOutcomes.ReplayConflict,
                $"{failed.Group.Replace(DependencyRelockPlan.GroupPrefix, string.Empty, StringComparison.Ordinal)} could not be regenerated after the replay ({failed.Blockers.FirstOrDefault()?.Message ?? failed.Message ?? failed.Status.ToString()}).",
                run.RelockPaths, ct);
            return;
        }
        var lookup = DependencyContract.Deserialize<LookupDocument>(data.LookupJson!)!;
        var section = DependenciesYml.Parse(await context.Forge.GetFileContentAsync(repository, run.ExpectedSha!, CoverageYml.FileName, ct)).Section;
        var patch = Convert.ToBase64String(files["dependency.patch"]);
        var relockJson = data.RelockPlanJson;
        // The relocked head still carries the dependency commit, workflow edits included (R15): same token as its first push.
        if (await WriterAsync(context, run.RequiresWorkflowsPermission, (access, credential) => writer.DependencyRollingAsync(Request(context, access, credential) with
            {
                ExpectedSha = run.RelockBaseSha, Author = Identity(run), Committer = Identity(run), Message = RelockMessage(relock),
                Plan = relockJson, Patch = patch, ClaimedGroups = claimed, AllowedHosts = section.Registries, Migrations = [],
                ExpectedRollingSha = run.RelockRollingSha, LastBotSha = null,
            }, ct), ct) is not { } result)
            return;
        if (result.Verdict == GitWriterDependencyOutcomes.NeedsHuman)
            run.PatchVerdict = GitWriterDependencyOutcomes.NeedsHuman;
        await AfterRollingAsync(context, data, Report(data), lookup, result, botSha: run.RelockBotSha, ct);
    }

    internal static string RelockMessage(DependencyPlan relock)
    {
        var message = new StringBuilder("Regenerate lockfiles after replaying commits on the dependency branch\n\n");
        foreach (var group in relock.Groups)
            message.Append("- ").Append(group.Group[DependencyRelockPlan.GroupPrefix.Length..]).Append('\n');
        message.Append('\n').Append(RollingBranchReplay.RelockTrailer).Append(' ').Append(relock.PlanId).Append('\n');
        return message.ToString();
    }

    /// <summary>The replay cannot go on: the branch is kept for a person, who is told why.</summary>
    private async Task ReplayStoppedAsync(Context context, string outcome, string detail, IReadOnlyList<string> paths, CancellationToken ct)
    {
        ClearRelock(context.Run);
        await CommentAsync(context, $"⚠️ {detail} {(paths.Count > 0 ? $"({string.Join(", ", paths.Take(10).Select(p => $"`{p}`"))}) " : string.Empty)}"
            + "The rolling branch was kept as it is; resolve it by hand, or close the pull request to let the bot start over.", ct);
        await EndAsync(context, outcome, detail, ct);
    }

    private static void ClearRelock(DependencyUpdateRun run)
    {
        run.RelockBaseSha = null;
        run.RelockBotSha = null;
        run.RelockRollingSha = null;
        run.RelockPaths = [];
    }

    private static string CommitMessage(DependencyRunReport report)
    {
        var message = new StringBuilder(report.Groups.Count == 1 ? $"Update {report.Groups[0].Group}" : $"Update dependencies ({report.Groups.Count} groups)");
        message.Append("\n\n");
        foreach (var item in report.Groups.SelectMany(g => g.Items.Select(i => (g.Group, i))).Take(100))
            message.Append($"- {item.Group}: {item.i.DepName} {item.i.From} -> {item.i.To} ({item.i.PackageFile})\n");
        return message.ToString();
    }

    private async Task UpsertPullRequestAsync(Context context, DependencyRunReport report, string? enqueueRefusal, CancellationToken ct)
    {
        var (repository, run) = (context.Repository, context.Run);
        ForgeBotPullRequest? pull = null;
        if (run.PullRequestNumber is { } number && await context.Updates.GetPullRequestAsync(repository, number, ct) is { IsOpen: true } open)
            pull = open;
        pull ??= await context.Updates.FindPullRequestAsync(repository, DependencyUpdateRun.RollingBranch, run.DefaultBranch!, ct);
        var title = DependencyPullRequestBody.Title(run, report);
        if (pull is null)
        {
            // R16: the first rolling pull request after onboarding is a draft, and never auto-merges.
            var draft = !run.FirstPullRequestOpened;
            run.PullRequestIsDraft = draft;
            var body = DependencyPullRequestBody.Render(run, report, enqueueRefusal);
            pull = await context.Updates.CreatePullRequestAsync(repository, DependencyUpdateRun.RollingBranch, run.DefaultBranch!, title, body, draft, ct);
            run.FirstPullRequestOpened = true;
            run.Note(Now, $"opened #{pull.Number}{(draft ? " as a draft" : string.Empty)}");
        }
        else
        {
            run.PullRequestIsDraft = pull.IsDraft;
            await context.Updates.UpdatePullRequestAsync(repository, pull.Number, title, DependencyPullRequestBody.Render(run, report, enqueueRefusal), ct);
        }
        run.PullRequestNumber = pull.Number;
    }

    private async Task UpdatePullRequestBodyAsync(Context context, DependencyRunReport report, string? enqueueRefusal, CancellationToken ct)
    {
        var run = context.Run;
        if (run.PullRequestNumber is not { } number) return;
        await BestEffortAsync(() => context.Updates.UpdatePullRequestAsync(context.Repository, number, DependencyPullRequestBody.Title(run, report),
            DependencyPullRequestBody.Render(run, report, enqueueRefusal), ct), "update the pull request body");
    }

    // ═══ Checks: red CI and bisect (N6), green and the merge queue (N7) ═════════════════════════════

    private async Task EvaluateChecksAsync(Context context, bool fromPoll, CancellationToken ct)
    {
        var (repository, run) = (context.Repository, context.Run);
        if (run.RollingSha is not { } sha) return;
        var observed = await context.Queue.GetCheckStateAsync(repository, sha, ct);
        // The updater's own check is in progress while it bisects; it and its suite never gate themselves.
        var ownSuites = observed.CheckRuns.Where(r => r.Name == IForgeDependencyUpdates.DependencyCheckName && r.SuiteId is not null).Select(r => r.SuiteId!.Value).ToHashSet();
        var state = observed with
        {
            CheckRuns = [.. observed.CheckRuns.Where(r => r.Name != IForgeDependencyUpdates.DependencyCheckName)],
            Suites = [.. observed.Suites.Where(s => !ownSuites.Contains(s.Id))],
        };
        var gate = CheckGate.Evaluate(state, [], [], []);
        var settling = run.Bisect is { Rerun: true, Excluded.Count: 0 } && run.StateChangedAtUtc is { } since && Now - since < RerunSettle;
        // Nothing reported yet is not green: a commit nobody checked proves nothing.
        if (gate.Verdict == ECheckVerdict.Pending || gate.Verdict == ECheckVerdict.Passed && gate.Passed.Count == 0 || settling)
        {
            if (fromPoll)
            {
                run.Polls++;
                await ScheduleAsync(run, EDependencyStep.PollChecks, ChecksPollInterval, ct);
            }
            return;
        }
        if (gate.Verdict == ECheckVerdict.Failed)
            await OnRedAsync(context, state, gate, forceBisect: false, ct);
        else
            await OnGreenAsync(context, ct);
    }

    private async Task OnRedAsync(Context context, ForgeCheckState state, CheckGateResult gate, bool forceBisect, CancellationToken ct)
    {
        var (repository, run) = (context.Repository, context.Run);
        var data = await DataAsync(context, ct);
        var report = Report(data);
        var failedRuns = state.CheckRuns.Where(r => gate.Failed.Contains(r.Name)).ToList();
        var bisect = run.Bisect;

        if (bisect is null)
        {
            var fleet = context.Account?.Id is { } accountId ? await stateStore.LoadFleetAsync(accountId, ct) : new DependencyFleetState();
            var budget = Repository.ResolveDependencyBisectBudget(repository, context.Account).Runs;
            bisect = run.Bisect = new DependencyBisect
            {
                Suspects = DependencyBisectPlanner.Suspects(report, fleet),
                Budget = forceBisect && budget == 0 ? 4 : budget,
                Sha = run.RollingSha,
                FailingChecks = [.. gate.Failed],
                FailureKind = DependencyCheckFailure.Classify(gate.Failed).ToString(),
                FailingRunUrl = failedRuns.Select(r => r.DetailsUrl).FirstOrDefault(u => u is not null),
                Items = [.. report.Groups.SelectMany(g => g.Items.Select(i => new DependencyBisectItem { Group = g.Group, Ecosystem = i.Ecosystem, DepName = i.DepName, Version = i.NewVersion }))],
            };
            if (bisect.Budget == 0 || bisect.Suspects.Count == 0)
            {
                await RedReportAsync(context, bisect, "bisecting is off for this repository (`DependencyBisectBudget`)", ct);
                return;
            }
            // Old §7.4: one re-run of the failing checks first, against flaky tests; it counts toward the budget.
            foreach (var failed in failedRuns)
                await BestEffortAsync(async () => await context.Queue.RerunCheckAsync(repository, failed, ct), "re-run a failed check");
            bisect.Rerun = true;
            bisect.Spent = 1;
            Transition(run, EDependencyRunState.Bisecting, $"CI failed ({string.Join(", ", gate.Failed)}); re-running the failed checks once");
            await PublishOwnCheckAsync(context, run.RollingSha!, EForgeQueueCheckState.InProgress, "Bisecting: don't merge", "CI failed; re-running the failed checks before bisecting.", ct);
            await UpdatePullRequestBodyAsync(context, report, null, ct);
            await ScheduleAsync(run, EDependencyStep.PollChecks, ChecksPollInterval, ct);
            return;
        }

        // Still red without the suspects tried so far: the one suspect never left out is the culprit.
        if (bisect.Suspects.Count - bisect.Excluded.Count == 1)
        {
            await CulpritAsync(context, bisect, bisect.Suspects.First(s => !bisect.Excluded.Contains(s)), ct);
            return;
        }
        if (DependencyBisectPlanner.NextExclusion(bisect) is not { } next)
        {
            await RedReportAsync(context, bisect, $"the bisect budget ({bisect.Budget} CI runs) is used up", ct);
            return;
        }
        bisect.Excluded = next;
        bisect.Spent++;
        Transition(run, EDependencyRunState.Planning, $"bisect step {bisect.Spent} of {bisect.Budget}: without {string.Join(", ", next)}");
        await ScheduleAsync(run, EDependencyStep.Plan, null, ct);
    }

    /// <summary>The culprit group is held (and its versions marked red for the fleet, Q15), then everything else is rebuilt.</summary>
    private async Task CulpritAsync(Context context, DependencyBisect bisect, string culprit, CancellationToken ct)
    {
        var (repository, run) = (context.Repository, context.Run);
        var data = await DataAsync(context, ct);
        var lookup = DependencyContract.Deserialize<LookupDocument>(data.LookupJson!)!;
        var items = bisect.Items.Where(i => i.Group == culprit).ToList();
        var state = await stateStore.LoadAsync(repository.Id!, ct);
        state.Holds.RemoveAll(h => h.Id == $"red:{culprit}");
        state.Holds.Add(new DependencyHold
        {
            Id = $"red:{culprit}", Kind = EDependencyHoldKind.Unattributed, Group = culprit,
            DepNames = [.. items.Select(i => i.DepName).Distinct(StringComparer.Ordinal)],
            Reason = $"CI went red with it{(bisect.FailingRunUrl is { } url ? $" ({url})" : string.Empty)}; found by bisect",
            Fingerprint = DependencyHolds.Fingerprint(lookup), CreatedAtUtc = Now, PlanId = run.PlanId,
        });
        if (context.Account?.Id is { } accountId && items.Count > 0)
        {
            var fleet = await stateStore.LoadFleetAsync(accountId, ct);
            foreach (var item in items)
            {
                fleet.RedVersions.RemoveAll(r => r.Ecosystem == item.Ecosystem && r.DepName == item.DepName && r.Version == item.Version && r.Repository == repository.Id);
                fleet.RedVersions.Add(new DependencyFleetRed { Ecosystem = item.Ecosystem, DepName = item.DepName, Version = item.Version, Repository = repository.Id!, RunUrl = bisect.FailingRunUrl, AtUtc = Now });
            }
            if (fleet.RedVersions.Count > DependencyFleetState.MaxEntries)
                fleet.RedVersions = [.. fleet.RedVersions.OrderByDescending(r => r.AtUtc).Take(DependencyFleetState.MaxEntries)];
        }
        bisect.Culprit = culprit;
        await CommentAsync(context, $"🔎 Bisect: `{culprit}` broke CI ({string.Join(", ", bisect.FailingChecks)}){(bisect.FailingRunUrl is { } run2 ? $" — {run2}" : string.Empty)}. "
            + $"{KindSentence(bisect)}It is held, and the rest is rebuilt without it.", ct);
        await PublishOwnCheckAsync(context, run.RollingSha!, EForgeQueueCheckState.Failure, $"{culprit} broke CI", $"Held: {culprit}. The pull request is rebuilt without it.", ct);
        run.PendingTrigger = EDependencyRunTrigger.Refresh;
        await EndAsync(context, "bisected", $"the culprit is {culprit}", ct);
    }

    private async Task RedReportAsync(Context context, DependencyBisect bisect, string why, CancellationToken ct)
    {
        var ranked = string.Join("\n", bisect.Suspects.Select((s, i) => $"{i + 1}. `{s}`{(bisect.Excluded.Contains(s) ? " (left out in a red test: not alone to blame)" : string.Empty)}"));
        await CommentAsync(context, $"❌ CI failed on this pull request ({string.Join(", ", bisect.FailingChecks.Select(c => $"`{c}`"))}){(bisect.FailingRunUrl is { } url ? $": {url}" : ".")} "
            + $"{KindSentence(bisect)}No culprit was found: {why}. The groups, most suspect first:\n\n{ranked}\n\nComment `hold <group>` to take one out, or `bisect` to try again.", ct);
        await PublishOwnCheckAsync(context, context.Run.RollingSha!, EForgeQueueCheckState.Failure, "CI failed", $"No culprit found: {why}.", ct);
        await EndAsync(context, "red", why, ct);
    }

    /// <summary>D-N3: "A build check failed. " from the failing checks' names, or nothing when they do not say.</summary>
    private static string KindSentence(DependencyBisect bisect)
    {
        var kind = Enum.TryParse<ECheckFailureKind>(bisect.FailureKind, out var parsed) ? parsed : ECheckFailureKind.Unknown;
        var text = DependencyCheckFailure.Describe(kind);
        return text.Length == 0 ? string.Empty : $"{char.ToUpperInvariant(text[0])}{text[1..]}. ";
    }

    private async Task OnGreenAsync(Context context, CancellationToken ct)
    {
        var (repository, run) = (context.Repository, context.Run);
        if (run.Bisect is { } bisect)
        {
            if (bisect.Excluded.Count > 0)
            {
                // Green without the suspects left out: the last one left out is the culprit (one culprit assumed; the rebuild
                // that follows tests the others again).
                await CulpritAsync(context, bisect, bisect.Excluded[^1], ct);
                return;
            }
            run.Note(Now, "green on the re-run: a flaky check, not an update");
            run.Bisect = null;
        }

        var data = await DataAsync(context, ct);
        var report = Report(data);
        var yml = await context.Forge.GetFileContentAsync(repository, run.DefaultBranch!, CoverageYml.FileName, ct);
        var section = DependenciesYml.Parse(yml).Section;
        var queueEnabled = MergeQueueYml.Parse(yml, run.DefaultBranch!).Section is { Enabled: true };
        var commit = await session.LoadAsync<Commit>(Commit.DocumentId(repository.Provider, repository.GitHubId, run.RollingSha!), ct);
        var (coverage, note) = DependencyCoverageGate.Evaluate(commit, section.MaxCoverageDrop);
        run.CoverageNote = note;

        var workflowFiles = report.Groups.SelectMany(g => g.Items).Select(i => i.PackageFile).Where(p => p.StartsWith(".github/workflows/", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToList();
        var notOnPullRequest = new List<string>();
        if (workflowFiles.Count > 0)
        {
            var files = await context.Queue.GetWorkflowFilesAsync(repository, run.DefaultBranch!, ct);
            notOnPullRequest.AddRange(workflowFiles.Where(path => !DependencyWorkflowGuard.RunsOnPullRequest(path, files.FirstOrDefault(f => f.Path == path)?.Content)));
        }
        var draft = run.PullRequestNumber is { } number && await context.Updates.GetPullRequestAsync(repository, number, ct) is { IsDraft: true };
        var refusal = run.Trigger == EDependencyRunTrigger.Refresh
            ? "A run a comment started does not auto-merge; the next scheduled run may."
            : DependencyAutoMergeGate.Refusal(new DependencyEnqueueInput(queueEnabled, report, draft, run.PatchVerdict == GitWriterDependencyOutcomes.NeedsHuman,
                run.MigratedGroups.Count > 0, notOnPullRequest, coverage));

        await PublishOwnCheckAsync(context, run.RollingSha!, EForgeQueueCheckState.Success, "CI is green", refusal ?? "Handed to the merge queue.", ct);
        if (refusal is null && run.PullRequestNumber is { } pr)
        {
            // R5: auto-merge is the merge queue, only; it re-checks the rules, the approvals and the checks itself.
            await outbox.EnqueueAsync(session, new MergeQueueSignalMessage
            {
                RepositoryId = repository.Id!, Kind = EMergeQueueSignal.Command, Number = pr, Command = EMergeQueueCommand.Enqueue,
                Actor = QueueActor, Source = EMergeQueueCommandSource.DependencyUpdates,
            }, null, ct);
            run.Note(Now, $"green: #{pr} handed to the merge queue");
        }
        await UpdatePullRequestBodyAsync(context, report, refusal, ct);
        await EndAsync(context, "green", refusal ?? "enqueued", ct);
    }

    private async Task ChecksTimedOutAsync(Context context, CancellationToken ct)
    {
        if (context.Run.RollingSha is { } sha)
            await PublishOwnCheckAsync(context, sha, EForgeQueueCheckState.Neutral, "Checks did not finish", "The bot stopped waiting for this commit's checks.", ct);
        await EndAsync(context, "checks-timeout", "The checks did not finish in time.", ct);
    }

    // ═══ Commands (§7.5, R6, gap E) ═══════════════════════════════════════════════════════════════

    private async Task CommandAsync(Context context, int number, long? commentId, EDependencyCommand command, string? argument, string actor, CancellationToken ct)
    {
        var (repository, run) = (context.Repository, context.Run);
        if (number != run.PullRequestNumber) return;
        // A writer, read from the forge per command (§7.5); anyone else gets 👎 and nothing happens.
        if (await context.Queue.GetActorRoleAsync(repository, actor, ct) < EForgeRepositoryRole.Write)
        {
            await ReactAsync(context, commentId, EForgeReaction.ThumbsDown, ct);
            run.Note(Now, $"@{actor}'s {Word(command)} was refused: no write access");
            return;
        }
        var data = await session.LoadAsync<DependencyUpdateRunData>(DependencyUpdateRunData.ForRepository(repository.Id!), ct);
        var report = data?.ReportJson is { } json ? JsonSerializer.Deserialize<DependencyRunReport>(json, JsonSerializerOptions.Web)! : new DependencyRunReport();
        var state = await stateStore.LoadAsync(repository.Id!, ct);

        switch (command)
        {
            case EDependencyCommand.Approve when argument is { } group:
                if (DependencyHolds.Approve(state, report.AsOutcome(state), group, run.ConfigBlob, actor, commentId, time.GetUtcNow()) is null)
                {
                    await ReactAsync(context, commentId, EForgeReaction.ThumbsDown, ct);
                    await CommentAsync(context, $"Nothing named `{group}` waits for approval (an update above an enforced account rule cannot be approved).", ct);
                    return;
                }
                run.Note(Now, $"@{actor} approved {group}");
                break;
            case EDependencyCommand.Hold when argument is { } target:
                var isGroup = report.Groups.Any(g => g.Group == target) || report.AwaitingApproval.Concat(report.Held).Concat(report.Available).Any(e => e.Group == target);
                DependencyHolds.AddCommandHold(state, target, isGroup, actor, time.GetUtcNow());
                run.Note(Now, $"@{actor} held {target}");
                break;
            case EDependencyCommand.Unhold when argument is { } target:
                if (!DependencyHolds.RemoveCommandHold(state, target))
                {
                    await ReactAsync(context, commentId, EForgeReaction.ThumbsDown, ct);
                    await CommentAsync(context, $"There is no comment or human hold on `{target}` (the bot's own holds lift by themselves).", ct);
                    return;
                }
                run.Note(Now, $"@{actor} unheld {target}");
                break;
            case EDependencyCommand.Bisect:
                await ReactAsync(context, commentId, EForgeReaction.ThumbsUp, ct);
                if (run.State.IsBusy() || run.RollingSha is not { } sha)
                {
                    await CommentAsync(context, "A run is going; `bisect` applies to a finished, red pull request.", ct);
                    return;
                }
                var observed = await context.Queue.GetCheckStateAsync(repository, sha, ct);
                var gate = CheckGate.Evaluate(observed with { CheckRuns = [.. observed.CheckRuns.Where(r => r.Name != IForgeDependencyUpdates.DependencyCheckName)] }, [], [], []);
                if (gate.Verdict != ECheckVerdict.Failed)
                {
                    await CommentAsync(context, "The checks of this pull request are not red; there is nothing to bisect.", ct);
                    return;
                }
                run.Bisect = null;
                await OnRedAsync(context, observed, gate, forceBisect: true, ct);
                return;
            case EDependencyCommand.Refresh:
                break;
            default:
                return;
        }
        await ReactAsync(context, commentId, EForgeReaction.ThumbsUp, ct);
        await TriggerAsync(context, EDependencyRunTrigger.Refresh, null, ct);
    }

    private async Task PullRequestEndedAsync(Context context, int number, bool wasMerged, CancellationToken ct)
    {
        var run = context.Run;
        if (number == run.OnboardingPullRequestNumber)
        {
            run.Onboarding = wasMerged ? EDependencyOnboarding.Done : EDependencyOnboarding.Blocked;
            run.OnboardingNote = wasMerged ? null : "The onboarding pull request was closed without merging.";
            run.Note(Now, wasMerged ? $"onboarding #{number} merged" : $"onboarding #{number} closed");
            if (wasMerged)
                await TriggerAsync(context, EDependencyRunTrigger.Refresh, null, ct);
            return;
        }
        if (number != run.PullRequestNumber) return;
        run.Note(Now, wasMerged ? $"#{number} merged" : $"#{number} closed");
        run.PullRequestNumber = null;
        run.RollingSha = null;
        run.RollingBotSha = null;
        run.CheckRunId = null;
        run.CheckSha = null;
        run.PullRequestIsDraft = false;
        run.Pins = [];
        run.ReplayRetried = false;
        // D-N1: a merged or closed pull request takes its human fixes with it; nothing is pinned for them any more.
        if (await session.LoadAsync<DependencyUpdateRunData>(DependencyUpdateRunData.ForRepository(context.Repository.Id!), ct) is { } data)
            data.RollingPlanJson = null;
        if (run.State is EDependencyRunState.AwaitingChecks or EDependencyRunState.Bisecting)
            await EndAsync(context, wasMerged ? "merged" : "closed", wasMerged ? "The pull request was merged." : "The pull request was closed.", ct);
    }

    // ═══ Onboarding (R16) ═══════════════════════════════════════════════════════════════════════════

    private async Task OnboardAsync(Context context, CancellationToken ct)
    {
        var (repository, run) = (context.Repository, context.Run);
        if (run.Onboarding is EDependencyOnboarding.Done or EDependencyOnboarding.PullRequestOpened || repository.DefaultBranch is not { } defaultBranch) return;
        if (!Repository.ResolveDependencyUpdates(repository, context.Account)) return;
        if (await context.Queue.GetFileBlobShaAsync(repository, defaultBranch, ".github/workflows/dependency-lookup.yml", ct) is not null)
        {
            run.Onboarding = EDependencyOnboarding.Done;
            run.Note(Now, "the dependency workflows were already on the default branch: no onboarding pull request");
            return;
        }
        var head = await context.Queue.GetBranchHeadAsync(repository, defaultBranch, ct)
            ?? throw new ForgeRequestException($"The head of {defaultBranch} in {repository.FullName} could not be read.", null);
        var tree = await context.Forge.GetTreeAsync(repository, defaultBranch, ct);
        var coverageYml = await context.Forge.GetFileContentAsync(repository, defaultBranch, CoverageYml.FileName, ct);
        string? dependabotPath = null, dependabotYml = null;
        foreach (var path in new[] { ".github/dependabot.yml", ".github/dependabot.yaml" })
        {
            if (await context.Forge.GetFileContentAsync(repository, defaultBranch, path, ct) is { } text)
            {
                (dependabotPath, dependabotYml) = (path, text);
                break;
            }
        }
        string? actionSha = null;
        try
        {
            actionSha = await context.Updates.ResolveTagCommitAsync(repository, DependencyOnboarding.ActionRepository, DependencyOnboarding.ActionTag, ct);
        }
        catch (Exception ex) when (ex is ForgeRequestException or ForgeAccessDeniedException or HttpRequestException)
        {
            logger.LogWarning(ex, "Dependency updates: could not resolve {Tag}; the onboarding templates keep the tag", DependencyOnboarding.ActionTag);
        }
        var plan = DependencyOnboarding.Plan([.. tree?.Entries.Select(e => e.Path) ?? []], coverageYml, dependabotPath, dependabotYml, actionSha, DependencyOnboarding.ReadTemplate);

        // §7.2: Workflows: write (and the rest of the feature's needs) must be declared by the App and accepted by the
        // installation before the templates can be pushed; say which, and where, instead of failing on the push.
        if (await PermissionGapAsync(context, ct) is { } gap)
        {
            Block(run, gap);
            return;
        }

        GitWriterDependencyResult? pushed;
        try
        {
            var job = await hook.RunWriterJobAsync(Saga(context), null, pushesWorkflowChanges: true, (access, credential) => writer.DependencyFilesAsync(
                new GitWriterDependencyRequest(repository.Provider.ToCanonicalString(), repository.GitHubId.ToString(CultureInfo.InvariantCulture), access.CloneUrl, credential, defaultBranch, head)
                {
                    Author = Identity(run), Committer = Identity(run), Message = DependencyOnboarding.Title, Files = plan.Files,
                }, ct), ct);
            pushed = job.Ran ? job.Result : null;
            if (!job.Ran)
            {
                Block(run, job.Refusal ?? "The forge gave no credential to push the onboarding branch.");
                return;
            }
        }
        catch (ForgeAccessDeniedException ex)
        {
            Block(run, $"The App could not push the onboarding branch ({ex.Message}). It needs **Workflows: write**: the installation's owner accepts the new permission on GitHub.");
            return;
        }
        catch (GitWriterRefusedException ex)
        {
            Block(run, ex.Message);
            return;
        }
        if (pushed is not { Outcome: GitWriterOutcomes.Pushed })
        {
            if (pushed?.Outcome is GitWriterOutcomes.Busy or GitWriterOutcomes.Failed)
                throw new GitWriterUnavailableException($"The git writer answered the onboarding commit with {pushed.Outcome}: {pushed.Detail}");
            Block(run, $"The onboarding branch was not pushed ({pushed?.Outcome}): {pushed?.Detail}");
            return;
        }

        var pull = await context.Updates.FindPullRequestAsync(repository, DependencyUpdateRun.OnboardingBranch, defaultBranch, ct);
        if (pull is null)
            pull = await context.Updates.CreatePullRequestAsync(repository, DependencyUpdateRun.OnboardingBranch, defaultBranch, DependencyOnboarding.Title, plan.Body, draft: false, ct);
        else
            await context.Updates.UpdatePullRequestAsync(repository, pull.Number, DependencyOnboarding.Title, plan.Body, ct);
        run.Onboarding = EDependencyOnboarding.PullRequestOpened;
        run.OnboardingPullRequestNumber = pull.Number;
        run.OnboardingNote = null;
        run.Note(Now, $"opened the onboarding pull request #{pull.Number}");
    }

    /// <summary>What the App lacks, or the installation has not accepted, of <see cref="EMergeQueueAppNeed.DependencyUpdates"/>;
    /// null when nothing is missing or nothing can be said (fail open: the push itself still refuses).</summary>
    private async Task<string?> PermissionGapAsync(Context context, CancellationToken ct)
    {
        if (permissionSources.FirstOrDefault(p => p.Provider == context.Repository.Provider) is not { } source || context.Account is not { } account)
            return null;
        try
        {
            if (await source.GetAppGapsAsync(EMergeQueueAppNeed.DependencyUpdates, ct) is { IsEmpty: false } gaps)
                return $"The App does not request {string.Join(", ", gaps.MissingPermissions.Select(p => $"{p.DisplayName}: {p.Declared}").Concat(gaps.MissingEvents.Select(e => $"the {e} event")))}, "
                    + $"which dependency updates need; the App's owner adds them{(gaps.SettingsUrl is { } url ? $" at {url}" : string.Empty)}.";
            if (await source.GetPendingAsync(account, ct) is { } pending && source.NeededPending(pending.Pending, EMergeQueueAppNeed.DependencyUpdates) is { Count: > 0 } needed)
                return $"The installation has not accepted {string.Join(", ", needed.Select(p => $"{p.DisplayName}: {p.Declared}"))} yet (Workflows: write lets the bot add the "
                    + $"workflow templates and bump Actions); accept them at {pending.HtmlUrl}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Dependency updates: could not read the App's permissions for {Repository}", context.Repository.FullName);
        }
        return null;
    }

    private void Block(DependencyUpdateRun run, string why)
    {
        run.Onboarding = EDependencyOnboarding.Blocked;
        run.OnboardingNote = why;
        run.Note(Now, $"onboarding blocked: {why}");
    }

    // ═══ Ending, cleanup ═══════════════════════════════════════════════════════════════════════════

    private async Task EndAsync(Context context, string outcome, string message, CancellationToken ct)
    {
        var run = context.Run;
        await hook.AbandonAsync(Saga(context), run.Runs, ct);
        await ScheduleCleanupAsync(context, ct);
        run.Outcome = outcome;
        run.OutcomeMessage = message;
        run.FinishedAtUtc = Now;
        Transition(run, EDependencyRunState.Idle, $"run {run.Run} ended: {outcome} ({message})");
        if (run.PendingTrigger is { } next)
        {
            BeginRun(context, next);
            await ScheduleAsync(run, EDependencyStep.Start, null, ct);
        }
    }

    /// <summary>R3/WrongCommit: the run starts over on the new head, a bounded number of times.</summary>
    private async Task RestartAsync(Context context, string why, CancellationToken ct)
    {
        var run = context.Run;
        if (run.Restarts >= MaxRestarts)
        {
            await EndAsync(context, "base-moving", $"{why}, {run.Restarts} times in a row; the next slot tries again.", ct);
            return;
        }
        await hook.AbandonAsync(Saga(context), run.Runs, ct);
        run.Restarts++;
        run.Runs = [];
        Transition(run, EDependencyRunState.Starting, $"{why}; starting over ({run.Restarts} of {MaxRestarts})");
        await ScheduleAsync(run, EDependencyStep.Start, null, ct);
    }

    /// <summary>R14: the work ref is deleted after every run, under a lease on what this run left there. Through the outbox,
    /// so it only happens once the state change commits.</summary>
    private async Task ScheduleCleanupAsync(Context context, CancellationToken ct)
    {
        var run = context.Run;
        if (run.WorkRefSha is not { } sha) return;
        run.WorkRefSha = null;
        await outbox.EnqueueAsync(session, new DependencyUpdateStepMessage
        {
            RepositoryId = run.Repository, Run = run.Run, Generation = run.Generation, Step = EDependencyStep.Cleanup, ExpectedSha = sha,
        }, null, ct);
    }

    private async Task CleanupAsync(Context context, string? expectedSha, CancellationToken ct)
    {
        if (!writer.IsConfigured || expectedSha is null) return;
        // A later run may already have pushed the work ref again; the lease keeps that one.
        if (string.Equals(context.Run.WorkRefSha, expectedSha, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var job = await hook.RunWriterJobAsync(Saga(context), null, pushesWorkflowChanges: false,
                (access, credential) => writer.DependencyDeleteWorkRefAsync(Request(context, access, credential) with { ExpectedSha = expectedSha }, ct), ct);
            if (job.Result is { Outcome: GitWriterOutcomes.Busy or GitWriterOutcomes.Failed } failed)
                throw new GitWriterUnavailableException($"The git writer answered the work-ref deletion with {failed.Outcome}: {failed.Detail}");
        }
        catch (Exception ex) when (ex is ForgeAccessDeniedException or GitWriterRefusedException)
        {
            logger.LogWarning(ex, "Dependency updates: could not delete {Ref} in {Repository}", DependencyUpdateRun.WorkBranch, context.Repository.FullName);
        }
    }

    // ═══ Helpers ════════════════════════════════════════════════════════════════════════════════════

    private async Task GuardedAsync(Context context, EDependencyStep retryStep, Func<Task> body, CancellationToken ct)
    {
        var run = context.Run;
        try
        {
            await body();
        }
        catch (ForgeAccessDeniedException ex)
        {
            await EndAsync(context, "permission", $"The forge refused a call the dependency updater needs{(ex.RequiredPermission is { } p ? $" (it asks for `{p}`)" : string.Empty)}: {ex.Message}", ct);
        }
        catch (GitWriterRefusedException ex)
        {
            await EndAsync(context, "writer-refused", $"The git writer refused the {ex.Job} request: `{ex.Field}` must be {ex.Rule}.", ct);
        }
        catch (Exception ex) when (WorkflowHook.IsTransient(ex, ct))
        {
            run.TransientFailures++;
            run.LastError = ex.Message;
            if (run.TransientFailures > MaxTransientRetries)
            {
                await EndAsync(context, "error", $"Gave up after {run.TransientFailures} failed attempts in a row: {ex.Message}", ct);
                return;
            }
            var delay = TimeSpan.FromSeconds(Math.Min(600, 15 * Math.Pow(2, run.TransientFailures - 1)));
            logger.LogWarning(ex, "Dependency updates: {Step} of {Repository} failed ({Failures}/{Max}); retrying in {Delay}", retryStep, context.Repository.FullName,
                run.TransientFailures, MaxTransientRetries + 1, delay);
            await ScheduleAsync(run, retryStep, delay, ct);
        }
    }

    /// <summary>One writer job with a token of its own (T9). Null: no token, and the run was ended with the forge's reason.</summary>
    private async Task<T?> WriterAsync<T>(Context context, bool pushesWorkflowChanges, Func<ForgeWriterAccess, SidecarCredential, Task<T>> job, CancellationToken ct) where T : class
    {
        if (!writer.IsConfigured)
        {
            await EndAsync(context, "writer-unavailable", "This server's git writer is not configured (MergeQueue:GitWriterUrl).", ct);
            return null;
        }
        var answer = await hook.RunWriterJobAsync(Saga(context), null, pushesWorkflowChanges, job, ct);
        if (answer.Ran) return answer.Result;
        await EndAsync(context, "push-refused", answer.Refusal ?? "The forge gave no credential to the git writer.", ct);
        return null;
    }

    private static GitWriterDependencyRequest Request(Context context, ForgeWriterAccess access, SidecarCredential credential)
        => new(context.Repository.Provider.ToCanonicalString(), context.Repository.GitHubId.ToString(CultureInfo.InvariantCulture), access.CloneUrl, credential,
            context.Run.DefaultBranch ?? context.Repository.DefaultBranch ?? "main", context.Run.ExpectedSha);

    /// <summary>Dated with the run's start, so every job of the run builds the same commit from the same request (§0 D65).</summary>
    private GitWriterIdentity Identity(DependencyUpdateRun run)
        => new(CommitterName, options.Value.CommitterEmail, new DateTimeOffset(DateTime.SpecifyKind(run.StartedAtUtc ?? Now, DateTimeKind.Utc)));

    private string RunToken(Context context)
        => $"{context.Repository.GitHubId.ToString(CultureInfo.InvariantCulture)}-{context.Run.Run.ToString(CultureInfo.InvariantCulture)}-{(context.Run.Runs.Count + 1).ToString(CultureInfo.InvariantCulture)}";

    private async Task<DependencyUpdateRunData> DataAsync(Context context, CancellationToken ct)
    {
        var id = DependencyUpdateRunData.ForRepository(context.Repository.Id!);
        var data = await session.LoadAsync<DependencyUpdateRunData>(id, ct);
        if (data is null)
        {
            data = new DependencyUpdateRunData { Id = id, Run = context.Run.Run };
            await session.StoreAsync(data, id, ct);
        }
        if (data.Run != context.Run.Run)
        {
            // A new run starts from nothing but its own lookup.
            data.Run = context.Run.Run;
            data.PlanJson = data.ReportJson = data.ApplyResultJson = data.Patch = data.RelockPlanJson = null;
            data.Migrations = [];
            data.ResolvedActions = new(StringComparer.Ordinal);
            data.TargetActions = new(StringComparer.Ordinal);
            if (context.Run.State is EDependencyRunState.Looking or EDependencyRunState.Starting)
                data.LookupJson = null;
        }
        return data;
    }

    private static DependencyRunReport Report(DependencyUpdateRunData data)
        => data.ReportJson is { } json ? JsonSerializer.Deserialize<DependencyRunReport>(json, JsonSerializerOptions.Web)! : new DependencyRunReport();

    private async Task CommentAsync(Context context, string body, CancellationToken ct)
    {
        if (context.Run.PullRequestNumber is not { } number) return;
        await BestEffortAsync(async () => await context.Queue.PostCommentAsync(context.Repository, number, body, ct), "comment on the pull request");
    }

    private async Task ReactAsync(Context context, long? commentId, EForgeReaction reaction, CancellationToken ct)
    {
        if (commentId is not { } id) return;
        await BestEffortAsync(() => context.Updates.AddCommentReactionAsync(context.Repository, id, reaction, ct), "react to a command");
    }

    private async Task PublishOwnCheckAsync(Context context, string sha, EForgeQueueCheckState state, string title, string summary, CancellationToken ct)
    {
        var run = context.Run;
        var existing = string.Equals(run.CheckSha, sha, StringComparison.OrdinalIgnoreCase) ? run.CheckRunId : null;
        try
        {
            run.CheckRunId = await context.Updates.PublishCheckAsync(context.Repository, sha, new ForgeQueueCheck(state, title, summary, null)
            {
                PullRequestNumber = run.PullRequestNumber,
            }, existing, ct);
            run.CheckSha = sha;
        }
        catch (Exception ex) when (ex is ForgeAccessDeniedException || WorkflowHook.IsTransient(ex, ct))
        {
            logger.LogWarning(ex, "Dependency updates: could not publish the check run on {Sha} in {Repository}", Short(sha), context.Repository.FullName);
        }
    }

    private async Task BestEffortAsync(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException or HttpRequestException)
        {
            logger.LogWarning(ex, "Dependency updates: could not {What}", what);
        }
    }

    private void Transition(DependencyUpdateRun run, EDependencyRunState state, string why)
    {
        run.State = state;
        run.Generation++;
        run.TransientFailures = 0;
        run.Polls = 0;
        run.StateChangedAtUtc = Now;
        run.Note(Now, why);
    }

    private Task ScheduleAsync(DependencyUpdateRun run, EDependencyStep step, TimeSpan? delay, CancellationToken ct)
        => outbox.EnqueueAsync(session, new DependencyUpdateStepMessage
        {
            RepositoryId = run.Repository, Run = run.Run, Generation = run.Generation, Step = step,
        }, delay is { } wait ? new BroadcastOptions { Delay = wait } : null, ct);

    private async Task<Context?> LoadContextAsync(string repositoryId, bool create, CancellationToken ct)
    {
        var repository = await session.LoadAsync<Repository>(repositoryId, ct);
        if (repository?.Id is null) return null;
        var queue = forgeQueues.FirstOrDefault(q => q.Provider == repository.Provider);
        var updates = forgeUpdates.FirstOrDefault(q => q.Provider == repository.Provider);
        if (queue is null || updates is null)
        {
            logger.LogDebug("Dependency updates: {Provider} has no support ({Repository})", repository.Provider, repository.FullName);
            return null;
        }
        var id = DependencyUpdateRun.ForRepository(repository.Id);
        var run = await session.LoadAsync<DependencyUpdateRun>(id, ct);
        if (run is null)
        {
            if (!create) return null;
            run = new DependencyUpdateRun { Id = id, Repository = repository.Id };
            await session.StoreAsync(run, id, ct);
        }
        var account = repository.Account is null ? null : await session.LoadAsync<Account>(repository.Account, ct);
        return new Context(repository, account, forges.For(repository), queue, updates, run);
    }

    private HookSaga Saga(Context context) => new(this, context);

    private sealed class HookSaga(DependencyUpdateEngine engine, Context context) : IWorkflowHookSaga
    {
        public Repository Repository => context.Repository;

        public IForgeMergeQueue Forge => context.Queue;

        public void Note(string text) => context.Run.Note(engine.Now, text);

        public Task ScheduleAsync(EWorkflowHookStep step, TimeSpan? delay, long? runId, CancellationToken cancellationToken) => step switch
        {
            EWorkflowHookStep.PollRun => engine.ScheduleAsync(context.Run, EDependencyStep.PollRun, delay, cancellationToken),
            EWorkflowHookStep.Timeout => engine.ScheduleAsync(context.Run, EDependencyStep.Timeout, delay, cancellationToken),
            EWorkflowHookStep.CancelRun => engine.outbox.EnqueueAsync(engine.session, new DependencyUpdateStepMessage
            {
                RepositoryId = context.Run.Repository, Run = context.Run.Run, Generation = context.Run.Generation, Step = EDependencyStep.CancelRun, RunId = runId,
            }, null, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
        };
    }

    private static string Word(EDependencyRunTrigger trigger) => trigger switch
    {
        EDependencyRunTrigger.Scheduled => "scheduled",
        EDependencyRunTrigger.Refresh => "refresh",
        EDependencyRunTrigger.OwnRelease => "own-release",
        _ => "security",
    };

    private static string Word(EDependencyCommand command) => command.ToString().ToLowerInvariant();

    private static string Short(string? sha) => sha is { Length: > 12 } ? sha[..12] : sha ?? "?";
}
