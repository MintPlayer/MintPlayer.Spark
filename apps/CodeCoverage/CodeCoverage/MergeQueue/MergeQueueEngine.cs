using System.Globalization;
using System.Text.RegularExpressions;
using CodeCoverage.CommitGraph;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.Git.MergeQueue;
using CodeCoverage.LookupReferences;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;
using QueueDocument = CodeCoverage.Entities.MergeQueue;

namespace CodeCoverage.MergeQueue;

/// <summary>The merge queue's saga (merge-queue PRD §5.1, §5.5, O20): what each step and each signal does.</summary>
public interface IMergeQueueEngine
{
    Task HandleSignalAsync(MergeQueueSignalMessage signal, CancellationToken cancellationToken = default);

    Task HandleStepAsync(MergeQueueStepMessage step, CancellationToken cancellationToken = default);
}

/// <summary>
/// The saga. Every handler loads the queue state, does at most one git-writer job (≤ 240 s, under the 5-minute claim),
/// changes the state and queues its next step through the outbox in the same <c>SaveChanges</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Serial by construction (D11).</b> Steps and signals share one messaging lane (<see cref="CoverageQueues.MergeQueue"/>),
/// so no two handlers ever change a queue at once; the session also runs with optimistic concurrency, so a second host
/// would fail its save rather than interleave.
/// </para>
/// <para>
/// <b>Decisions live here, facts in the forge.</b> <see cref="IForgeMergeQueue"/> answers roles, rules, checks and runs;
/// O9's <c>write</c> floor, O11's approval floor, O12's gate, D8/D16's rules check, O18's warnings and O22's bypass role are
/// this class's, and <see cref="MergeQueueWorkflowGuard"/> decides the dispatch (§0 D63).
/// </para>
/// <para>
/// <b>Failures.</b> A forge or writer failure that may pass (5xx, rate limit, writer busy) re-runs the same step later,
/// with backoff, up to <see cref="MaxTransientRetries"/> times, then evicts. A refused permission evicts at once with the
/// "accept the new permissions" link (O17), and so does a request the git writer refuses (§0 D57), which would be refused
/// again. Everything the queue acts on (a conflict, a stale head, a failed run) is a typed outcome, never an exception.
/// </para>
/// </remarks>
[Register(typeof(IMergeQueueEngine), ServiceLifetime.Scoped)]
public partial class MergeQueueEngine : IMergeQueueEngine
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IForgeIntegrationResolver forges;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> forgeQueues;
    [Inject] private readonly IEnumerable<IForgeInstallationPermissions> permissionSources;
    [Inject] private readonly IGitWriterSidecar writer;
    [Inject] private readonly IMessageOutbox outbox;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly IOptions<MergeQueueOptions> options;
    [Inject] private readonly IConfiguration configuration;
    [Inject] private readonly ILogger<MergeQueueEngine> logger;

    /// <summary>Retries of a step after a transient failure; the failure after the last retry evicts (§0 D58).</summary>
    public const int MaxTransientRetries = 6;
    public const int MaxPrepareAttempts = 5;
    public const int MaxMergePolls = 40;
    internal static readonly TimeSpan RunPollInterval = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan ChecksPollInterval = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan HeadModifiedRetryDelay = TimeSpan.FromSeconds(15);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>What every handler works with: the repository, its forge's two seams and its queue document.</summary>
    private sealed record Context(Repository Repository, Account? Account, IForgeIntegration Forge, IForgeMergeQueue Queue, QueueDocument Document);

    // ═══ Signals ═════════════════════════════════════════════════════════════════════════════════════

    public async Task HandleSignalAsync(MergeQueueSignalMessage signal, CancellationToken cancellationToken = default)
    {
        session.Advanced.UseOptimisticConcurrency = true;
        var ct = cancellationToken;

        if (signal.Kind == EMergeQueueSignal.Command)
        {
            if (signal.Number is not { } number || string.IsNullOrEmpty(signal.Actor) || signal.Command is not { } command) return;
            if (await LoadContextAsync(signal.RepositoryId, create: command == EMergeQueueCommand.Enqueue, ct) is not { } commandContext) return;
            if (command == EMergeQueueCommand.Enqueue)
                await EnqueueAsync(commandContext, number, signal.Actor, signal.Source ?? EMergeQueueCommandSource.Comment, signal.Label, ct);
            else
                await DequeueAsync(commandContext, number, signal.Actor, signal.Source ?? EMergeQueueCommandSource.Comment, signal.Label, ct);
            await session.SaveChangesAsync(ct);
            return;
        }

        if (await LoadContextAsync(signal.RepositoryId, create: false, ct) is not { } context) return;

        switch (signal.Kind)
        {
            case EMergeQueueSignal.HeadPushed when signal.Number is { } number && signal.Sha is { } sha:
                if (await LoadActiveAsync(signal.RepositoryId, number, ct) is not { } pushed || pushed.KnownShas.Contains(sha, StringComparer.OrdinalIgnoreCase))
                    break;
                if (pushed.State == EMergeQueueEntryState.Publishing)
                {
                    // §0 D52: while publishing, a head the queue does not know may still be its own commit, pushed by a
                    // publish whose answer never arrived (the app died before saving it, or the response was lost). The
                    // writer can tell, by the same criteria as its retried publish; only its "branch moved" evicts.
                    pushed.Note(Now, $"the branch moved to {Short(sha)} while publishing; asking the writer whether that is the queue's own commit");
                    await GuardedAsync(context, pushed, EMergeQueueStep.Publish, () => PublishAsync(context, pushed, ct), ct);
                    break;
                }
                // T3. No PRD id in what people read (§0 D72): the eviction message goes on the check run and in a comment.
                await EvictAsync(context, pushed, "author-push",
                    $"The pull request's branch moved to `{sha}`, a commit the queue did not write. The queue merges only the commit that was queued: queue it again to merge the new one.", ct: ct);
                break;

            case EMergeQueueSignal.Closed when signal.Number is { } number:
                if (await LoadActiveAsync(signal.RepositoryId, number, ct) is { } closed)
                    await EndAsync(context, closed, EMergeQueueEntryState.Dequeued, "The pull request was closed.", ct);
                break;

            case EMergeQueueSignal.Merged when signal.Number is { } number:
                if (await LoadActiveAsync(signal.RepositoryId, number, ct) is { } merged)
                {
                    if (merged.State == EMergeQueueEntryState.Merging)
                        await CompleteAsync(context, merged, null, ct);
                    else
                        await EndAsync(context, merged, EMergeQueueEntryState.Dequeued, "The pull request was merged outside the queue.", ct);
                }
                break;

            case EMergeQueueSignal.BranchPushed when signal.Branch is { } branch && signal.Sha is { } baseSha:
                foreach (var entry in await LoadCurrentEntriesAsync(context, ct))
                {
                    if (entry.BaseBranch != branch || entry.BaseSha is null || string.Equals(entry.BaseSha, baseSha, StringComparison.OrdinalIgnoreCase)) continue;
                    // O20 / §5.1: a base that moves while the run waits sends it back to prepare; it is not the PR's fault.
                    if (entry.State is EMergeQueueEntryState.Regenerating or EMergeQueueEntryState.AwaitingChecks)
                        await RestartAsync(context, entry, $"{branch} moved to {Short(baseSha)}; rebasing again.", ct);
                }
                break;

            case EMergeQueueSignal.RunCompleted when signal.RunId is { } runId:
                foreach (var entry in await LoadCurrentEntriesAsync(context, ct))
                    if (entry.State == EMergeQueueEntryState.Regenerating && entry.DispatchRunId == runId)
                        await GuardedAsync(context, entry, EMergeQueueStep.PollRun, () => OnRunAsync(context, entry, fromPoll: false, ct), ct);
                break;

            case EMergeQueueSignal.ChecksChanged when signal.Sha is { } checkedSha:
                foreach (var entry in await LoadCurrentEntriesAsync(context, ct))
                    if (entry.State == EMergeQueueEntryState.AwaitingChecks && string.Equals(entry.FinalSha, checkedSha, StringComparison.OrdinalIgnoreCase))
                        await GuardedAsync(context, entry, EMergeQueueStep.PollChecks, () => EvaluateChecksAsync(context, entry, fromPoll: false, ct), ct);
                break;

            case EMergeQueueSignal.Reviewed when signal.Number is { } number:
                if (await LoadActiveAsync(signal.RepositoryId, number, ct) is { State: EMergeQueueEntryState.AwaitingChecks } reviewed)
                    await GuardedAsync(context, reviewed, EMergeQueueStep.PollChecks, () => EvaluateChecksAsync(context, reviewed, fromPoll: false, ct), ct);
                break;
        }

        await session.SaveChangesAsync(ct);
    }

    // ═══ Steps ═══════════════════════════════════════════════════════════════════════════════════════

    public async Task HandleStepAsync(MergeQueueStepMessage step, CancellationToken cancellationToken = default)
    {
        session.Advanced.UseOptimisticConcurrency = true;
        var ct = cancellationToken;

        var entry = await session.LoadAsync<MergeQueueEntry>(step.EntryId, ct);
        if (entry is null) return;
        if (await LoadContextAsync(entry.Repository, create: false, ct) is not { } context) return;

        if (step.Step == EMergeQueueStep.Cleanup)
        {
            await CleanupAsync(context, entry, step, ct);
            await session.SaveChangesAsync(ct);
            return;
        }
        if (step.Step == EMergeQueueStep.CancelRun)
        {
            await CancelRunAsync(context, entry, step, ct);
            await session.SaveChangesAsync(ct);
            return;
        }

        // Idempotency: a step for another run, or for a state this run has left, is a no-op.
        if (entry.Run != step.Run || entry.Generation != step.Generation || !Expects(step.Step, entry.State))
        {
            logger.LogDebug("Merge queue: ignoring {Step} for {Entry} (run {Run} gen {Generation}; entry is run {EntryRun} gen {EntryGeneration}, {State})",
                step.Step, entry.Id, step.Run, step.Generation, entry.Run, entry.Generation, entry.State);
            return;
        }

        await GuardedAsync(context, entry, step.Step, step.Step switch
        {
            EMergeQueueStep.Start => () => StartAsync(context, entry, ct),
            EMergeQueueStep.ResetQueueBranch => () => ResetQueueBranchAsync(context, entry, ct),
            EMergeQueueStep.Dispatch => () => DispatchAsync(context, entry, ct),
            EMergeQueueStep.PollRun => () => OnRunAsync(context, entry, fromPoll: true, ct),
            EMergeQueueStep.Publish => () => PublishAsync(context, entry, ct),
            EMergeQueueStep.PollChecks => () => EvaluateChecksAsync(context, entry, fromPoll: true, ct),
            EMergeQueueStep.Merge => () => MergeAsync(context, entry, ct),
            EMergeQueueStep.PollMerge => () => PollMergeAsync(context, entry, ct),
            EMergeQueueStep.Timeout => () => TimeoutAsync(context, entry, ct),
            _ => () => Task.CompletedTask,
        }, ct);

        await session.SaveChangesAsync(ct);
    }

    private static bool Expects(EMergeQueueStep step, EMergeQueueEntryState state) => step switch
    {
        EMergeQueueStep.Start or EMergeQueueStep.ResetQueueBranch or EMergeQueueStep.Dispatch => state == EMergeQueueEntryState.Preparing,
        EMergeQueueStep.PollRun => state == EMergeQueueEntryState.Regenerating,
        EMergeQueueStep.Publish => state == EMergeQueueEntryState.Publishing,
        EMergeQueueStep.PollChecks => state == EMergeQueueEntryState.AwaitingChecks,
        EMergeQueueStep.Merge or EMergeQueueStep.PollMerge => state == EMergeQueueEntryState.Merging,
        EMergeQueueStep.Timeout => state is EMergeQueueEntryState.Regenerating or EMergeQueueEntryState.AwaitingChecks,
        _ => false,
    };

    // ═══ Enqueue and dequeue (O8, O9, O11, D8, D16, O18, T1, T2) ═══════════════════════════════════

    private async Task EnqueueAsync(Context context, int number, string actor, EMergeQueueCommandSource source, string? label, CancellationToken ct)
    {
        var repository = context.Repository;
        var id = MergeQueueEntry.DocumentId(repository.Id!, number);
        var entry = await session.LoadAsync<MergeQueueEntry>(id, ct);

        // A replayed label or a second /queue for a pull request already in the queue changes nothing.
        if (entry is not null && !entry.State.IsTerminal())
        {
            logger.LogDebug("Merge queue: #{Number} in {Repository} is already queued (run {Run})", number, repository.FullName, entry.Run);
            return;
        }

        // O16: the queue's configuration comes from the DEFAULT branch, never from the pull request's base.
        var defaultBranch = repository.DefaultBranch;
        var yml = defaultBranch is null ? null : await context.Forge.GetFileContentAsync(repository, defaultBranch, CoverageYml.FileName, ct);
        var parsed = MergeQueueYml.Parse(yml, defaultBranch ?? "main");

        // A label speaks only when it is the configured one, on a repository that turned the queue on. Anything else is
        // somebody else's label and is ignored without a word; a comment or the button always gets an answer.
        if (source == EMergeQueueCommandSource.Label)
        {
            if (!string.Equals(label, parsed.Section?.Label ?? "merge-queue", StringComparison.Ordinal)) return;
            if (parsed.Errors.Count == 0 && parsed.Section is not { Enabled: true }) return;
        }

        var pull = await context.Forge.GetPullRequestAsync(repository, number, ct)
            ?? throw new ForgeRequestException($"Pull request #{number} of {repository.FullName} could not be read.", null);

        if (entry is null)
        {
            entry = new MergeQueueEntry { Id = id, Repository = repository.Id!, Number = number };
            await session.StoreAsync(entry, id, ct);
        }
        BeginRun(entry, pull, actor, source);
        entry.Config = parsed.Section?.Snapshot($"{defaultBranch}", repository, context.Account) ?? new MergeQueueConfigSnapshot();

        try
        {
            if (await RefusalAsync(context, entry, pull, parsed, actor, ct) is { } refusal)
            {
                await RefuseAsync(context, entry, refusal.Code, refusal.Message, source, label, ct);
                return;
            }
        }
        catch (ForgeAccessDeniedException ex)
        {
            await RefuseAsync(context, entry, "permission", $"The forge refused a call the queue needs: {ex.Message}", source, label, ct, permissions: true);
            return;
        }

        Transition(entry, EMergeQueueEntryState.Queued, $"queued by @{actor} ({source.ToString().ToLowerInvariant()})");
        context.Document.LaneFor(entry.BaseBranch).Waiting.Add(entry.Id!);
        await PublishCheckAsync(context, entry, ct);
        await StartNextAsync(context, entry.BaseBranch, ct);
    }

    /// <summary>Resets the per-pull-request document for a new run, keeping the earlier runs' outcomes.</summary>
    private void BeginRun(MergeQueueEntry entry, ForgePullRequest pull, string actor, EMergeQueueCommandSource source)
    {
        if (entry.Run > 0)
        {
            entry.PastRuns.Add(new MergeQueuePastRun
            {
                Run = entry.Run, State = entry.State, Reason = entry.EvictionReason, PinnedHeadSha = entry.PinnedHeadSha,
                MergeCommitSha = entry.MergeCommitSha, FinishedAtUtc = entry.FinishedAtUtc,
            });
            if (entry.PastRuns.Count > MergeQueueEntry.MaxPastRuns) entry.PastRuns.RemoveAt(0);
        }

        entry.Run++;
        entry.Generation++;
        entry.BaseBranch = pull.BaseRef;
        entry.PinnedHeadSha = pull.HeadSha;
        entry.ExpectedHeadSha = pull.HeadSha;
        entry.KnownShas = [pull.HeadSha];
        entry.EnqueuedBy = actor;
        entry.EnqueuedVia = source;
        entry.HeadBranch = pull.HeadRef;
        entry.IsFork = pull.IsFromFork;
        entry.AuthorLogin = pull.AuthorLogin;
        entry.Title = pull.Title;
        entry.CoAuthors = null;
        entry.EnqueuedAtUtc = Now;
        entry.StateChangedAtUtc = Now;
        entry.StartedAtUtc = null;
        entry.FinishedAtUtc = null;
        entry.BaseSha = null;
        entry.PreparedSha = null;
        // An earlier run's queue branch is its own Cleanup's to delete (D5); this run's push leases on "absent" and resets
        // a branch it finds instead (§0 D65).
        entry.QueueBranchSha = null;
        entry.ResolvedPaths = [];
        entry.BaseChangedWorkflows = false;
        entry.WorkflowPaths = [];
        entry.WorkflowGuardBypassed = false;
        entry.DispatchRunId = null;
        entry.DispatchRunUrl = null;
        entry.DispatchRunFinished = false;
        entry.FinalSha = null;
        entry.DirectMerge = false;
        entry.RequiredChecks = [];
        entry.ExpectedChecks = [];
        entry.CheckRetries = 0;
        entry.MergeRequestId = null;
        entry.MergeCommitSha = null;
        entry.HeadModifiedRetries = 0;
        entry.PrepareAttempts = 0;
        entry.TransientFailures = 0;
        entry.LastError = null;
        entry.Polls = 0;
        entry.EvictionReason = null;
        entry.EvictionMessage = null;
        entry.EvictionPaths = [];
        entry.PermissionsUrl = null;
        entry.Warnings = [];
        entry.History = [];
        // QueueCheckSha/QueueCheckId are kept: a new run on the same head updates the check run it already has.
    }

    private sealed record Refusal(string Code, string Message);

    /// <summary>Every enqueue condition, in the order a person would want to hear about them. Null: accepted.</summary>
    private async Task<Refusal?> RefusalAsync(Context context, MergeQueueEntry entry, ForgePullRequest pull, MergeQueueYmlResult parsed, string actor, CancellationToken ct)
    {
        var repository = context.Repository;
        if (parsed.Errors.Count > 0)
            return new("configuration", "`coverage.yml` on the default branch has errors in `mergeQueue:`:\n" + string.Join("\n", parsed.Errors.Select(e => $"- {e}")));
        if (parsed.Section is not { Enabled: true } section)
            return new("disabled", "The merge queue is not enabled: `coverage.yml` on the default branch needs `mergeQueue: { enabled: true }`.");
        if (!pull.IsOpen)
            return new("closed", "The pull request is closed.");
        if (pull.IsDraft)
            return new("draft", "The pull request is a draft.");
        // O16. Here and below, what people read names no PRD id: they never saw the PRD (§0 D72). The ids stay in comments.
        if (!section.Branches.Contains(pull.BaseRef, StringComparer.Ordinal))
            return new("branch", $"`{pull.BaseRef}` is not a queue branch: `coverage.yml` on the default branch lists {string.Join(", ", section.Branches.Select(b => $"`{b}`"))}.");

        // O9 / T2: the actor needs the write role; a triage user's label or a fork author's comment is refused.
        var role = await context.Queue.GetActorRoleAsync(repository, actor, ct);
        if (role < EForgeRepositoryRole.Write)
            return new("role", $"@{actor} needs at least write access to queue a pull request (has {role.ToString().ToLowerInvariant()}).");
        var bypass = Bypasses(role, entry.Config.BypassRole);

        // O3: the queue pushes the rebased commit to the fork through "Allow edits by maintainers".
        if (pull.IsFromFork && (pull.HeadRepositoryFullName is null || !pull.MaintainerCanModify))
            return new("fork", pull.HeadRepositoryFullName is null
                ? "The pull request's fork no longer exists."
                : $"The queue pushes the rebased commit to {pull.HeadRepositoryFullName}, which needs \"Allow edits by maintainers\" ticked on the pull request. Forks owned by an organization cannot tick it.");

        // O22: the bypass role skips the queue's own approval floor (re-checked before the merge, like the role itself).
        if (bypass)
            entry.Note(Now, $"@{actor} has the {role.ToString().ToLowerInvariant()} role, which bypasses the approval floor");
        else if (await ApprovalFloorRefusalAsync(context, entry, ct) is { } floor)
            return floor;

        // D8, D16, T7, O18: read the base branch's rules once, at enable time for this run.
        var rules = await context.Queue.GetBranchRulesAsync(repository, pull.BaseRef, ct);
        if (rules.HasNativeMergeQueue)
            return new("native-queue", $"`{pull.BaseRef}` requires GitHub's own merge queue. Turn that rule off: the two queues cannot share a branch.");
        // §0 D62: the queue's own check is in progress while the queue merges, so a branch that requires it never merges.
        if (rules.RequiredChecks.Contains(CheckGate.QueueCheckName, StringComparer.Ordinal))
            return new("self-required", $"`{pull.BaseRef}` requires the \"{CheckGate.QueueCheckName}\" check, which is the queue's own: it is still in progress while the queue merges, "
                + "so GitHub would refuse every merge the queue asks for. Remove it from the branch's required checks (ruleset or branch protection).");
        foreach (var ruleset in rules.Rulesets.Where(r => r.RuleTypes.Any(t => t is "pull_request" or "required_status_checks")))
        {
            if (ruleset.Bypass != EForgeRulesetBypass.Never)
                return new("bypass", ruleset.Bypass == EForgeRulesetBypass.Unknown
                    ? $"Ruleset '{ruleset.Name ?? ruleset.Id.ToString(CultureInfo.InvariantCulture)}' holds the reviews or checks, and whether the App can bypass it could not be read. The queue must be held to it."
                    : $"The App can bypass ruleset '{ruleset.Name ?? ruleset.Id.ToString(CultureInfo.InvariantCulture)}' ({ruleset.Bypass}), which holds the reviews or checks. Remove the App from its bypass list: only the \"Restrict updates\" ruleset may exempt it.");
        }
        if (rules.DismissStaleReviewsOnPush)
            entry.Warnings.Add("The branch dismisses stale approvals on push. The queue's regenerate commit changes the diff, so GitHub would dismiss the approval and the merge would be refused; turn \"dismiss stale approvals\" off for queue branches.");
        if (rules.RequireLastPushApproval)
            entry.Warnings.Add("The branch requires approval of the most recent push by someone else. The queue's commit is the most recent push, so the merge would be refused; turn that rule off for queue branches.");
        entry.RequiredChecks = [.. rules.RequiredChecks.Order(StringComparer.Ordinal)];

        // O12's expected minimum (§0 D45): the checks the pull request's own CI reported on the pinned head.
        entry.ExpectedChecks = [.. await context.Queue.GetCheckNamesOnCommitAsync(repository, pull.HeadSha, ct)];
        return null;
    }

    /// <summary>
    /// O11, at enqueue and again right before the merge (§0 D61). An approval counts only when it is the reviewer's latest
    /// word, was given on the pinned head (or on a commit the queue wrote from it), comes from someone other than the author
    /// and from a <c>write</c> user (§0 D47). A <c>write</c> user whose latest word is "changes requested" vetoes the merge
    /// under either floor, approvals or not (§0 D61). <see cref="EMergeQueueApprovalFloor.RepositoryRules"/> has no floor of
    /// the queue's own, so neither applies there.
    /// </summary>
    private async Task<Refusal?> ApprovalFloorRefusalAsync(Context context, MergeQueueEntry entry, CancellationToken ct)
    {
        var floor = entry.Config.ApprovalFloor;
        if (floor == EMergeQueueApprovalFloor.RepositoryRules) return null;

        var author = entry.AuthorLogin;
        bool IsAuthor(string login) => string.Equals(login, author, StringComparison.OrdinalIgnoreCase);
        var latest = (await context.Queue.GetReviewsAsync(context.Repository, entry.Number, ct))
            .Where(r => r.ReviewerLogin is not null && r.State is not (EForgeReviewState.Commented or EForgeReviewState.Pending or EForgeReviewState.Other))
            .GroupBy(r => r.ReviewerLogin!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .Where(r => !IsAuthor(r.ReviewerLogin!))
            .ToList();

        foreach (var objection in latest.Where(r => r.State == EForgeReviewState.ChangesRequested))
            if (await context.Queue.GetActorRoleAsync(context.Repository, objection.ReviewerLogin!, ct) >= EForgeRepositoryRole.Write)
                return new("approval", $"@{objection.ReviewerLogin} has write access and requested changes. The queue does not merge while a writer's latest review requests changes.");

        if (floor == EMergeQueueApprovalFloor.ExternalAuthors && author is not null
            && await context.Queue.GetActorRoleAsync(context.Repository, author, ct) >= EForgeRepositoryRole.Write)
            return null;

        var approvers = latest
            .Where(r => r.State == EForgeReviewState.Approved && r.CommitSha is { } sha && entry.KnownShas.Contains(sha, StringComparer.OrdinalIgnoreCase))
            .Select(r => r.ReviewerLogin!);
        foreach (var approver in approvers)
            if (await context.Queue.GetActorRoleAsync(context.Repository, approver, ct) >= EForgeRepositoryRole.Write)
                return null;

        return new("approval", floor == EMergeQueueApprovalFloor.Always
            ? $"This repository's approval floor needs an approval of commit `{Short(entry.PinnedHeadSha)}` by someone with write access other than the author."
            : $"@{author ?? "the author"} does not have write access, so an approval of commit `{Short(entry.PinnedHeadSha)}` by someone with write access is needed.");
    }

    private async Task RefuseAsync(Context context, MergeQueueEntry entry, string code, string message, EMergeQueueCommandSource source, string? label, CancellationToken ct, bool permissions = false)
    {
        Transition(entry, EMergeQueueEntryState.Refused, $"refused: {code}");
        entry.EvictionReason = code;
        entry.EvictionMessage = message;
        entry.FinishedAtUtc = Now;
        context.Document.Record(Outcome(entry));
        if (permissions) entry.PermissionsUrl = await PermissionsUrlAsync(context, ct);
        await PublishCheckAsync(context, entry, ct);

        if (source == EMergeQueueCommandSource.Label)
            await BestEffortAsync(() => context.Queue.RemoveLabelAsync(context.Repository, entry.Number, label ?? entry.Config.Label, ct), "remove the label");
        else
            await BestEffortAsync(() => context.Queue.PostCommentAsync(context.Repository, entry.Number,
                $"The merge queue did not queue this pull request: {message}", ct), "comment");
        logger.LogInformation("Merge queue: refused #{Number} in {Repository}: {Code}", entry.Number, context.Repository.FullName, code);
    }

    private async Task DequeueAsync(Context context, int number, string actor, EMergeQueueCommandSource source, string? label, CancellationToken ct)
    {
        if (await LoadActiveAsync(context.Repository.Id!, number, ct) is not { } entry) return;
        if (source == EMergeQueueCommandSource.Label && !string.Equals(label, entry.Config.Label, StringComparison.Ordinal)) return;

        // The author may always withdraw; anyone else needs the role that could have queued it (T2).
        var mayDequeue = string.Equals(actor, entry.AuthorLogin, StringComparison.OrdinalIgnoreCase)
            || await context.Queue.GetActorRoleAsync(context.Repository, actor, ct) >= EForgeRepositoryRole.Write;
        if (!mayDequeue)
        {
            entry.Note(Now, $"@{actor} asked to dequeue without write access; ignored");
            return;
        }

        await EndAsync(context, entry, EMergeQueueEntryState.Dequeued, $"Removed from the queue by @{actor}.", ct);
    }

    // ═══ Step 1: start (O20 case 1) or prepare ═══════════════════════════════════════════════════════

    private async Task StartAsync(Context context, MergeQueueEntry entry, CancellationToken ct)
    {
        var repository = context.Repository;
        var pull = await ReadPullRequestAsync(context, entry, ct);
        if (pull is null) return;

        var baseSha = await context.Queue.GetBranchHeadAsync(repository, entry.BaseBranch, ct);
        if (baseSha is null)
        {
            await EvictAsync(context, entry, "base-missing", $"The base branch `{entry.BaseBranch}` no longer exists.", ct: ct);
            return;
        }

        // §0 D60: once per run, while the branch still holds the pull request's own commits (after PUBLISH it holds the
        // queue's squash, whose trailers carry them over to a later run).
        entry.CoAuthors ??= [.. CoAuthorTrailers(await context.Queue.GetPullRequestCommitsAsync(repository, entry.Number, ct), entry.AuthorLogin, options.Value.CommitterEmail)];

        // O20 case 1: the head already contains the latest base, so the commit to merge is the head itself. Its checks
        // decide; nothing is rebased, published or regenerated.
        var comparison = await context.Forge.CompareAsync(repository, baseSha, entry.ExpectedHeadSha, ct);
        if (comparison?.MergeBaseSha is { } mergeBase && string.Equals(mergeBase, baseSha, StringComparison.OrdinalIgnoreCase))
        {
            entry.BaseSha = baseSha;
            entry.FinalSha = entry.ExpectedHeadSha;
            entry.DirectMerge = true;
            await EnterAwaitingChecksAsync(context, entry, "the head already contains the latest base", ct);
            return;
        }

        if (!writer.IsConfigured)
        {
            await EvictAsync(context, entry, "writer-unavailable", "The pull request needs a rebase, and this server's git writer is not configured (MergeQueue:GitWriterUrl).", ct: ct);
            return;
        }
        // §0 D57: the rebased commit is published to the pull request's branch, and the git writer refuses a name outside
        // its rules. GitHub allows more (a '+', for one), so this is checked here rather than refused by the writer later.
        if (!MergeQueueWireRules.IsBranch(entry.HeadBranch))
        {
            await EvictAsync(context, entry, "branch-name",
                $"The pull request needs a rebase, and the queue's git writer does not push to a branch named like this one ({MergeQueueWireRules.BranchRule}). "
                + "Rename the branch, or rebase it by hand and queue it again.", ct: ct);
            return;
        }
        // T9 (§0 D59): one job, one token. The prepare pushes nothing (§0 D65), so it never needs the workflow permission.
        if (await WriterAccessAsync(context, entry, pullRequest: null, pushesWorkflowChanges: false, ct) is not { } access) return;

        var result = await WithAccessAsync(context, access, credential => writer.PrepareAsync(PrepareRequest(context, entry, access, credential, baseSha), ct));

        switch (result.Outcome)
        {
            case GitWriterOutcomes.Prepared when result.PreparedSha is { } prepared:
                entry.BaseSha = result.BaseSha ?? baseSha;
                entry.PreparedSha = prepared;
                entry.ResolvedPaths = [.. result.ResolvedPaths];
                entry.BaseChangedWorkflows = result.BaseChangedWorkflows;
                entry.WorkflowPaths = result.TouchesWorkflows ? [.. result.Paths] : [];
                Remember(entry, prepared);
                // O22: the bypass role matters here only for a same-repository change under .github/** (a fork's never
                // bypasses, O25); the enqueuer's role is read now, when it decides.
                var bypass = result.TouchesWorkflows && !entry.IsFork
                    && Bypasses(await context.Queue.GetActorRoleAsync(repository, entry.EnqueuedBy, ct), entry.Config.BypassRole);
                // §0 D63, D67: whether to dispatch is decided in one place, together with what a .github/** change may do.
                var plan = MergeQueueWorkflowGuard.AfterPrepare(entry.Config, entry.ResolvedPaths.Count, result.TouchesWorkflows, entry.IsFork, bypass);
                entry.WorkflowGuardBypassed = plan == EMergeQueuePlan.Dispatch && result.TouchesWorkflows;
                switch (plan)
                {
                    case EMergeQueuePlan.Dispatch:
                        Transition(entry, EMergeQueueEntryState.Preparing, $"prepared {Short(prepared)} on {Short(entry.BaseSha)}; regenerating {entry.ResolvedPaths.Count} resolved path(s)"
                            + (entry.WorkflowGuardBypassed ? $"; its .github/** change runs with them because @{entry.EnqueuedBy} has the bypass role" : string.Empty));
                        await ScheduleAsync(entry, EMergeQueueStep.Dispatch, null, ct);
                        break;
                    case EMergeQueuePlan.Publish:
                        // §0 D65: nothing is dispatched, so the queue branch is never created. A .github/** change goes
                        // through (O21 A; a fork's too, O25).
                        Transition(entry, EMergeQueueEntryState.Publishing, $"prepared {Short(prepared)} on {Short(entry.BaseSha)}; nothing to regenerate"
                            + (result.TouchesWorkflows ? "; its .github/** change goes through, as no workflow will be dispatched" : string.Empty));
                        await ScheduleAsync(entry, EMergeQueueStep.Publish, null, ct);
                        break;
                    case EMergeQueuePlan.EvictForkTouchesWorkflows:
                        await EvictAsync(context, entry, ForkTouchesWorkflows, ForkTouchesWorkflowsMessage(entry, "Nothing was pushed"), result.Paths, ct: ct);
                        return;
                    default:
                        // D12, O21 A; the admin's merge is O21 B.
                        await EvictAsync(context, entry, "touches-workflows",
                            "The pull request changes files under `.github/` and needs the regenerate workflow, whose run would execute the queue branch's "
                            + "workflow files with this repository's context. Regenerate and rebase by hand, or have an admin merge it by hand.",
                            result.Paths, ct: ct);
                        return;
                }
                await PublishCheckAsync(context, entry, ct);
                break;

            case GitWriterOutcomes.Evicted:
                await EvictWriterAsync(context, entry, result.Reason, result.Detail, result.Paths, ct);
                break;

            case GitWriterOutcomes.HeadMismatch:
                await EvictAsync(context, entry, "author-push", $"The pull request's head is no longer the queued commit `{Short(entry.ExpectedHeadSha)}`. Queue it again to merge the new one.", ct: ct);
                break;

            case GitWriterOutcomes.BaseMoved:
                await RestartAsync(context, entry, $"{entry.BaseBranch} moved while preparing; rebasing again.", ct);
                break;

            default:
                throw new GitWriterUnavailableException($"The git writer answered prepare with {result.Outcome}: {result.Detail}");
        }
    }

    /// <summary>
    /// The writer's <c>evicted</c>: a conflict it may not resolve (O1, O2), from a prepare or from a rebuild. The writer's
    /// detail says why (for a file outside the allow-list, <see cref="MergeQueueResolver.OutsideAllowListDetail"/>), so this
    /// text only says what to do: it used to repeat the reason, and the comment then said it twice (§0 D72). The comment's
    /// own closing line says how to queue it again.
    /// </summary>
    private Task EvictWriterAsync(Context context, MergeQueueEntry entry, string? reason, string? detail, IReadOnlyList<string> paths, CancellationToken ct)
        => EvictAsync(context, entry, reason ?? "conflict",
            $"Rebasing onto `{entry.BaseBranch}` conflicts in files the queue may not resolve ({reason}). Rebase it onto `{entry.BaseBranch}` by hand."
            + (detail is { Length: > 0 } text ? $"\n\n{text}" : string.Empty),
            paths, ct: ct);

    /// <summary>O25: a fork's change under <c>.github/**</c> that needs the regenerate workflow. Never bypassed.</summary>
    internal const string ForkTouchesWorkflows = "fork-touches-workflows";

    /// <summary>Why a fork's <c>.github/**</c> change ends the entry before the dispatch (O25), and what to do instead: a
    /// head that already contains the latest base is merged as it is (O20 case 1), or an admin merges it (O21 B).</summary>
    private static string ForkTouchesWorkflowsMessage(MergeQueueEntry entry, string what)
        => "The pull request comes from a fork, changes files under `.github/` (its own change, or one a rename on the base carries there), and needs "
           + $"the regenerate workflow. That workflow runs on `{entry.QueueBranch}`, a branch of this repository, and pushing the fork's workflow files there "
           + $"would run them with this repository's secrets. {what}, and no role bypasses this. Rebase it onto `{entry.BaseBranch}` and regenerate the "
           + "files by hand (a head that already contains the latest base is merged as it is), or have an admin merge it.";

    /// <summary>
    /// The fields of the prepare, which the queue-branch push and the publish repeat so the writer rebuilds the same commit
    /// (§0 D65). Every date is the run's <see cref="MergeQueueEntry.EnqueuedAtUtc"/>, to the second: a date git stamped
    /// itself would make each job build another commit. <paramref name="baseSha"/>: for the prepare, the base branch's
    /// head as the saga read it; for the others, the base the prepared commit was built on.
    /// </summary>
    private GitWriterPrepareRequest PrepareRequest(Context context, MergeQueueEntry entry, ForgeWriterAccess access, SidecarCredential credential, string? baseSha)
    {
        var date = CommitDate(entry);
        var login = entry.AuthorLogin ?? "unknown";
        return new GitWriterPrepareRequest(
            Provider: context.Repository.Provider.ToCanonicalString(),
            RepoId: context.Repository.GitHubId.ToString(CultureInfo.InvariantCulture),
            CloneUrl: access.CloneUrl,
            Credential: credential,
            BaseBranch: entry.BaseBranch,
            BaseSha: baseSha,
            HeadRef: PullHeadRef(entry),
            HeadSha: entry.ExpectedHeadSha,
            AutoResolve: [.. entry.Config.AutoResolve.Select(r => r.Keys.Count == 0
                ? new GitWriterAutoResolve(r.Path, AnyLine: true)
                : new GitWriterAutoResolve(r.Path, r.Keys))],
            Author: new GitWriterIdentity(login, options.Value.AuthorEmailFormat.Replace("{login}", login, StringComparison.Ordinal), date),
            Committer: new GitWriterIdentity(options.Value.CommitterName, options.Value.CommitterEmail, date),
            Message: CommitMessage(entry));
    }

    /// <summary>The run's commit date (§0 D65): when it was queued, to the whole second, in UTC.</summary>
    internal static DateTimeOffset CommitDate(MergeQueueEntry entry)
    {
        var utc = DateTime.SpecifyKind(entry.EnqueuedAtUtc, DateTimeKind.Utc);
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    private async Task ResetQueueBranchAsync(Context context, MergeQueueEntry entry, CancellationToken ct)
    {
        var pull = await ReadPullRequestAsync(context, entry, ct);
        if (pull is null) return;
        // T9: the queue branch lives in the base repository; deleting it needs nothing but contents.
        if (await WriterAccessAsync(context, entry, pullRequest: null, pushesWorkflowChanges: false, ct) is not { } access) return;

        var result = await WithAccessAsync(context, access, credential => writer.DeleteQueueBranchAsync(new GitWriterDeleteQueueBranchRequest(
            context.Repository.Provider.ToCanonicalString(), context.Repository.GitHubId.ToString(CultureInfo.InvariantCulture),
            access.CloneUrl, credential, entry.QueueBranch, ExpectedSha: null), ct));
        switch (result.Outcome)
        {
            case GitWriterOutcomes.Deleted:
                entry.PreparedSha = null;
                entry.QueueBranchSha = null;
                await RestartAsync(context, entry, $"reset {entry.QueueBranch}", ct);
                break;
            case GitWriterOutcomes.PushRejected:
                await EvictAsync(context, entry, "push-rejected", $"The forge refused to delete `{entry.QueueBranch}`: {result.Detail}", ct: ct);
                break;
            default:
                throw new GitWriterUnavailableException($"The git writer answered delete with {result.Outcome}: {result.Detail}");
        }
    }

    /// <summary>Back to prepare on the latest base (O20: a moved base is never the pull request's fault), bounded.</summary>
    private async Task RestartAsync(Context context, MergeQueueEntry entry, string why, CancellationToken ct)
    {
        entry.PrepareAttempts++;
        if (entry.PrepareAttempts > MaxPrepareAttempts)
        {
            await EvictAsync(context, entry, "base-churn", $"The base moved {MaxPrepareAttempts} times during this run; queue it again when `{entry.BaseBranch}` is quieter.", ct: ct);
            return;
        }
        await AbandonRunAsync(entry, ct);
        entry.FinalSha = null;
        entry.DirectMerge = false;
        entry.DispatchRunId = null;
        entry.DispatchRunUrl = null;
        entry.DispatchRunFinished = false;
        entry.MergeRequestId = null;
        entry.CheckRetries = 0;
        entry.HeadModifiedRetries = 0;
        Transition(entry, EMergeQueueEntryState.Preparing, why);
        await ScheduleAsync(entry, EMergeQueueStep.Start, null, ct);
        await PublishCheckAsync(context, entry, ct);
    }

    // ═══ Step 2: regenerate (D12, D14) ═══════════════════════════════════════════════════════════════

    private async Task DispatchAsync(Context context, MergeQueueEntry entry, CancellationToken ct)
    {
        var repository = context.Repository;
        var file = entry.Config.PostRebaseActionName!;
        var path = $".github/workflows/{file}";
        var defaultBranch = repository.DefaultBranch ?? entry.BaseBranch;

        // §0 D65 (d): the dispatch needs the prepared commit on the queue branch, a branch of this repository. For a fork's
        // pull request that is the fork's code, and it goes there only when nothing there can run it with the repository's
        // secrets. A same-repository pull request was pushed by someone with write access, whose own push ran all that.
        if (entry.IsFork)
        {
            var pushed = await context.Queue.GetWorkflowFilesAsync(repository, entry.BaseSha!, ct);
            var atDefaultBranch = await context.Queue.GetWorkflowFilesAsync(repository, defaultBranch, ct);
            if (!atDefaultBranch.Any(f => string.Equals(f.Path, path, StringComparison.Ordinal)))
            {
                await EvictAsync(context, entry, "workflow-missing", $"`{path}` does not exist on the default branch, so the regenerate step cannot run.", ct: ct);
                return;
            }
            var findings = MergeQueueForkGuard.Evaluate(pushed, entry.BaseBranch, atDefaultBranch, defaultBranch, file, entry.QueueBranch);
            if (findings.Count > 0)
            {
                await EvictAsync(context, entry, MergeQueueForkGuard.ReasonCode, ForkRefusal(entry, findings), [.. findings.Select(f => f.Path).Distinct(StringComparer.Ordinal)], ct: ct);
                return;
            }
            entry.Note(Now, $"the fork precondition holds: no workflow runs on a push of {entry.QueueBranch}, and {file} is read-only");
        }

        // §0 D65 (b): the queue branch is written now, and only for a dispatch.
        if (!await PushQueueBranchAsync(context, entry, ct)) return;

        // D12: the run executes the workflow file of the dispatched ref. A change under .github/** reaches a dispatch only
        // through O22's bypass role (a same-repository pull request, §0 D63); either way, the regenerate workflow itself
        // must be byte-for-byte the default branch's, and that is never bypassed.
        var atDefault = await context.Queue.GetFileBlobShaAsync(repository, defaultBranch, path, ct);
        if (atDefault is null)
        {
            await EvictAsync(context, entry, "workflow-missing", $"`{path}` does not exist on the default branch, so the regenerate step cannot run.", ct: ct);
            return;
        }
        var atQueue = await context.Queue.GetFileBlobShaAsync(repository, entry.PreparedSha!, path, ct);
        if (!string.Equals(atQueue, atDefault, StringComparison.Ordinal))
        {
            await EvictAsync(context, entry, "workflow-differs",
                $"`{path}` on the queue commit is not the default branch's. The queue only runs the default branch's workflow; bring `{entry.BaseBranch}` in line with it.", ct: ct);
            return;
        }

        await CancelStrayRunsAsync(context, entry, file, ct);

        var dispatch = await context.Queue.DispatchWorkflowAsync(repository, file, entry.QueueBranch, new Dictionary<string, string>
        {
            ["expected_sha"] = entry.PreparedSha!,
            ["pr_number"] = entry.Number.ToString(CultureInfo.InvariantCulture),
            ["queue_run_id"] = $"{entry.Number}.{entry.Run}.{entry.PrepareAttempts}",
        }, ct);

        if (dispatch.Outcome != EForgeDispatchOutcome.Dispatched)
        {
            await EvictAsync(context, entry, "dispatch-refused", $"The forge refused to run `{file}` on `{entry.QueueBranch}` ({dispatch.Outcome}): {dispatch.Message}", ct: ct);
            return;
        }
        if (dispatch.RunId is not { } runId)
        {
            await EvictAsync(context, entry, "dispatch-uncorrelated", $"The forge started `{file}` without saying which run it is, so its result cannot be trusted.", ct: ct);
            return;
        }

        entry.DispatchRunId = runId;
        entry.DispatchRunUrl = dispatch.HtmlUrl;
        entry.DispatchRunFinished = false;
        Transition(entry, EMergeQueueEntryState.Regenerating, $"dispatched {file} run {runId}");
        await ScheduleAsync(entry, EMergeQueueStep.PollRun, RunPollInterval, ct);
        await ScheduleAsync(entry, EMergeQueueStep.Timeout, TimeSpan.FromMinutes(entry.Config.PostRebaseTimeoutMinutes), ct);
        await PublishCheckAsync(context, entry, ct);
    }

    /// <summary>
    /// §0 D65 (b): the prepared commit onto the queue branch, rebuilt by the writer from the prepare's own fields and
    /// checked against <see cref="MergeQueueEntry.PreparedSha"/>, under a lease on what this run pushed there before.
    /// True: the branch holds it; false: the entry was evicted, restarted, or sent to reset the branch.
    /// </summary>
    private async Task<bool> PushQueueBranchAsync(Context context, MergeQueueEntry entry, CancellationToken ct)
    {
        if (!writer.IsConfigured)
        {
            await EvictAsync(context, entry, "writer-unavailable", "This server's git writer is not configured (MergeQueue:GitWriterUrl).", ct: ct);
            return false;
        }
        // T9 (§0 D59): the queue branch is in the base repository. It carries workflow files only when O22's bypass role
        // let a same-repository change under .github/workflows/ through to the dispatch.
        var workflowFiles = entry.WorkflowPaths.Any(MergeQueueWorkflowGuard.IsWorkflowFile);
        if (await WriterAccessAsync(context, entry, pullRequest: null, workflowFiles, ct) is not { } access) return false;

        // O25, §0 D67: the writer is told where the head lives, and refuses a fork's .github change on its own.
        var result = await WithAccessAsync(context, access, credential => writer.PushQueueBranchAsync(new GitWriterQueueBranchRequest(
            PrepareRequest(context, entry, access, credential, entry.BaseSha), entry.PreparedSha!, entry.QueueBranch, entry.QueueBranchSha, entry.IsFork), ct));
        switch (result.Outcome)
        {
            case GitWriterOutcomes.Pushed:
                entry.QueueBranchSha = entry.PreparedSha;
                if (result.Reason == GitWriterReasons.AlreadyPushed)
                    entry.Note(Now, $"{entry.QueueBranch} already held {Short(entry.PreparedSha)}: an earlier attempt's push");
                return true;
            case GitWriterOutcomes.ForkTouchesWorkflows:
                // The plan never dispatches this (MergeQueueWorkflowGuard); the writer's own line held where it did.
                logger.LogError("Merge queue: the git writer refused to push a fork's .github change to {Branch} in {Repository}; the plan let it through",
                    entry.QueueBranch, context.Repository.FullName);
                await EvictAsync(context, entry, ForkTouchesWorkflows,
                    ForkTouchesWorkflowsMessage(entry, "The git writer refused the push, so nothing was pushed")
                    + (result.Detail is { Length: > 0 } detail ? $"\n\n{detail}" : string.Empty),
                    result.Paths, ct: ct);
                return false;
            case GitWriterOutcomes.QueueBranchMoved:
                Transition(entry, EMergeQueueEntryState.Preparing, $"{entry.QueueBranch} is out of step; resetting it");
                await ScheduleAsync(entry, EMergeQueueStep.ResetQueueBranch, null, ct);
                return false;
            case GitWriterOutcomes.PreparedDiffers:
                await EvictAsync(context, entry, "prepared-differs", $"The git writer rebuilt the prepared commit for `{entry.QueueBranch}` and got another one: {result.Detail}", ct: ct);
                return false;
            case GitWriterOutcomes.HeadMismatch:
                await EvictAsync(context, entry, "author-push", $"The pull request's head is no longer the queued commit `{Short(entry.ExpectedHeadSha)}`. Queue it again to merge the new one.", ct: ct);
                return false;
            case GitWriterOutcomes.BaseMoved:
                await RestartAsync(context, entry, $"the base the prepared commit was built on is gone from {entry.BaseBranch}; rebasing again.", ct);
                return false;
            case GitWriterOutcomes.Evicted:
                await EvictWriterAsync(context, entry, result.Reason, result.Detail, result.Paths, ct);
                return false;
            case GitWriterOutcomes.PushRejected:
                await EvictAsync(context, entry, "push-rejected", $"The forge refused the push to `{entry.QueueBranch}`: {result.Detail}", ct: ct);
                return false;
            default:
                throw new GitWriterUnavailableException($"The git writer answered the queue-branch push with {result.Outcome}: {result.Detail}");
        }
    }

    /// <summary>§0 D65: why the queue will not push this fork's commit to its queue branch, and what to change.</summary>
    private string ForkRefusal(MergeQueueEntry entry, IReadOnlyList<ForkGuardFinding> findings)
    {
        const int shown = 10;
        var list = string.Join("\n", findings.Take(shown).Select(f => $"- {f}"));
        if (findings.Count > shown)
            list += $"\n- and {findings.Count - shown} more.";
        var guide = GuideUrl("regenerate") is { } url ? $"the [setup guide]({url})" : "the merge queue's setup guide (\"The regenerate workflow\")";
        return $"The pull request comes from a fork and needs the regenerate workflow, so the queue would have to push the fork's code to `{entry.QueueBranch}` "
               + "in this repository first. It does that only when nothing there can run that code with this repository's secrets, and these "
               + $"workflows could:\n{list}\n\nFix them, then queue it again; {guide} shows a safe setup.";
    }

    /// <summary>A section of the setup guide (<c>/merge-queue/setup#…</c>); null unless the base URL is https.</summary>
    private string? GuideUrl(string anchor)
    {
        var baseUrl = configuration["Coverage:BaseUrl"];
        if (baseUrl is null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) || root.Scheme != Uri.UriSchemeHttps) return null;
        return $"{baseUrl.TrimEnd('/')}/merge-queue/setup#{anchor}";
    }

    /// <summary>
    /// §0 D53: a dispatch whose answer never arrived (the app died, or the response was lost) may have started a run the
    /// entry never learnt the id of, which nothing would cancel. Right before a dispatch the entry waits for no run, so any
    /// run of the workflow still going on the entry's own queue branch is such a stray (or an abandoned run whose
    /// cancellation is still on its way): it is cancelled rather than paid for twice. Best effort: a failure here never
    /// holds the dispatch back.
    /// </summary>
    private async Task CancelStrayRunsAsync(Context context, MergeQueueEntry entry, string file, CancellationToken ct)
    {
        try
        {
            foreach (var run in await context.Queue.ListWorkflowRunsAsync(context.Repository, file, entry.QueueBranch, ct))
                if (!run.IsCompleted && await context.Queue.CancelWorkflowRunAsync(context.Repository, run.Id, ct))
                    entry.Note(Now, $"cancelled the stray regenerate run {run.Id}, started by a dispatch whose answer never arrived");
        }
        catch (Exception ex) when (ex is ForgeAccessDeniedException || IsTransient(ex, ct))
        {
            logger.LogWarning(ex, "Merge queue: could not look for stray runs of {Workflow} on {Branch} in {Repository}", file, entry.QueueBranch, context.Repository.FullName);
        }
    }

    private async Task OnRunAsync(Context context, MergeQueueEntry entry, bool fromPoll, CancellationToken ct)
    {
        var run = await context.Queue.GetWorkflowRunAsync(context.Repository, entry.DispatchRunId!.Value, ct);
        if (run is null)
        {
            entry.DispatchRunFinished = true;
            await EvictAsync(context, entry, "regenerate-missing", $"The regenerate run {entry.DispatchRunId} no longer exists.", ct: ct);
            return;
        }
        if (!run.IsCompleted)
        {
            if (fromPoll) await ScheduleAsync(entry, EMergeQueueStep.PollRun, RunPollInterval, ct);
            return;
        }
        // Its own completion: whatever follows, there is nothing left to cancel (§0 D53).
        entry.DispatchRunFinished = true;

        // D14: the run built the branch head as of the dispatch; a run of any other commit proves nothing about ours.
        if (!string.Equals(run.HeadSha, entry.PreparedSha, StringComparison.OrdinalIgnoreCase))
        {
            await EvictAsync(context, entry, "regenerate-wrong-commit", $"The regenerate run built `{Short(run.HeadSha)}`, not the prepared `{Short(entry.PreparedSha)}`.", ct: ct);
            return;
        }
        if (run.Conclusion != "success")
        {
            await EvictAsync(context, entry, "regenerate-failed",
                $"The regenerate run finished `{run.Conclusion ?? "without a conclusion"}`{(run.HtmlUrl is { } url ? $": {url}" : ".")} Regenerate by hand and queue it again.", ct: ct);
            return;
        }

        Transition(entry, EMergeQueueEntryState.Publishing, $"regenerate run {run.Id} succeeded");
        await ScheduleAsync(entry, EMergeQueueStep.Publish, null, ct);
    }

    // ═══ Step 3: publish (T3, T4, O3) ═══════════════════════════════════════════════════════════════

    private async Task PublishAsync(Context context, MergeQueueEntry entry, CancellationToken ct)
    {
        var repository = context.Repository;
        // An unknown head is the writer's call here (§0 D52): it may be the commit an earlier attempt published.
        var pull = await ReadPullRequestAsync(context, entry, ct, headJudgedByWriter: true);
        if (pull is null) return;

        // The contract of .github/actions/spark-regenerate (D35): a green run with an empty patch means nothing to
        // regenerate; a missing artifact means the step did not do its job.
        string? patch = null;
        if (entry.DispatchRunId is { } runId)
        {
            var artifact = await context.Queue.DownloadArtifactAsync(repository, runId, options.Value.PatchArtifactName, options.Value.MaxPatchBytes, ct);
            if (artifact.Outcome != EForgeArtifactOutcome.Ok)
            {
                await EvictAsync(context, entry, "regenerate-artifact",
                    $"The regenerate run's `{options.Value.PatchArtifactName}` artifact is unusable ({artifact.Outcome}): {artifact.Detail}", ct: ct);
                return;
            }
            if (artifact.Content is { Length: > 0 } bytes) patch = Convert.ToBase64String(bytes);
        }

        if (!writer.IsConfigured)
        {
            await EvictAsync(context, entry, "writer-unavailable", "This server's git writer is not configured (MergeQueue:GitWriterUrl).", ct: ct);
            return;
        }
        // T9 (§0 D59, D71): the workflow permission only where it can matter, a same-repository change to the workflows.
        if (await WriterAccessAsync(context, entry, pull, PublishPushesWorkflowChanges(entry), ct) is not { } access) return;

        // §0 D65: the writer rebuilds the prepared commit from the prepare's own fields (the queue branch exists only when a
        // dispatch needed it) and publishes only when it is PreparedSha; the lease is the head the prepare was built from.
        var result = await WithAccessAsync(context, access, credential => writer.PublishAsync(new GitWriterPublishRequest(
            Prepare: PrepareRequest(context, entry, access, credential, entry.BaseSha),
            PreparedSha: entry.PreparedSha!,
            PushUrl: entry.IsFork ? access.PushUrl : null,
            PrBranch: entry.HeadBranch,
            Patch: patch,
            RegeneratedPaths: entry.Config.RegeneratedPaths), ct));

        switch (result.Outcome)
        {
            case GitWriterOutcomes.Published when result.FinalSha is { } final:
                entry.FinalSha = final;
                entry.ExpectedHeadSha = final;
                Remember(entry, final);
                await EnterAwaitingChecksAsync(context, entry, $"published {Short(final)}{(patch is null ? string.Empty : " with the regenerated files")}"
                    + (result.Reason == GitWriterReasons.AlreadyPushed ? " (found on the branch: an earlier attempt's push)" : string.Empty), ct);
                break;

            case GitWriterOutcomes.BranchMoved:
                await EvictAsync(context, entry, "author-push", $"The pull request's branch moved during the queue run. {result.Detail}", ct: ct);
                break;

            case GitWriterOutcomes.BaseMoved:
                await RestartAsync(context, entry, $"the base the prepared commit was built on is gone from {entry.BaseBranch}; rebasing again.", ct);
                break;

            case GitWriterOutcomes.PreparedDiffers:
                await EvictAsync(context, entry, "prepared-differs", $"The git writer rebuilt the prepared commit to publish it and got another one: {result.Detail}", ct: ct);
                break;

            case GitWriterOutcomes.Evicted:
                await EvictWriterAsync(context, entry, result.Reason, result.Detail, result.Paths, ct);
                break;

            case GitWriterOutcomes.PatchRejected:
                await EvictAsync(context, entry, "patch-rejected",
                    $"The regenerate patch was refused ({result.Reason}): it may only touch `regeneratedPaths`, as plain text files. {result.Detail}", result.Paths, ct: ct);
                break;

            // §0 D69, D71: the App may push to a fork through "Allow edits by maintainers" (S6, measured live), but GitHub
            // never lets it create or update a workflow file the fork has on no branch, whatever the token asks for. Only a
            // person can bring those files into the branch, so that refusal gets its own reason and says how.
            case GitWriterOutcomes.PushRejected when entry.IsFork && IsForkWorkflowRefusal(result.Detail):
                await EvictAsync(context, entry, ForkWorkflowUpdateNeeded, ForkWorkflowUpdateNeededMessage(entry, result.Detail), ct: ct);
                break;

            case GitWriterOutcomes.PushRejected when entry.IsFork:
                await EvictAsync(context, entry, "fork-push-rejected", $"GitHub refused the push to the fork's branch: {result.Detail}", ct: ct);
                break;

            case GitWriterOutcomes.PushRejected:
                await EvictAsync(context, entry, "push-rejected", $"The forge refused the push to `{entry.HeadBranch}`: {result.Detail}", ct: ct);
                break;

            default:
                throw new GitWriterUnavailableException($"The git writer answered publish with {result.Outcome}: {result.Detail}");
        }
    }

    /// <summary>§0 D71: a fork's publish GitHub refused because it would write workflow files the fork lacks (D69).</summary>
    internal const string ForkWorkflowUpdateNeeded = "fork-workflow-update-needed";

    /// <summary>
    /// GitHub's refusal of an App's push that creates or updates a workflow file, as git prints the remote's reason in the
    /// writer's push-rejected detail: "refusing to allow a GitHub App to create or update workflow `&lt;path&gt;` without
    /// `workflows` permission" (§0 D69, measured live). For a fork it means a file the fork has on no branch, which no
    /// token scoped to the base repository can write: GitHub evaluates the permission against the fork.
    /// </summary>
    internal static bool IsForkWorkflowRefusal(string? detail)
        => detail?.Contains("refusing to allow a GitHub App to create or update workflow", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>What a person does about <see cref="ForkWorkflowUpdateNeeded"/>: GitHub's own "Update branch" (or merging the
    /// base into the branch) brings the workflow files in as their push, and the next run publishes without writing any.</summary>
    private static string ForkWorkflowUpdateNeededMessage(MergeQueueEntry entry, string? detail)
        => (entry.BaseChangedWorkflows
               ? $"`{entry.BaseBranch}` changed workflow files that the fork's branch does not have yet, "
               : "The rebased commit carries workflow files that the fork's branch does not have yet, ")
           + "and GitHub does not let an App write workflow files into a fork, whatever its permissions. "
           + $"Click \"Update branch\" on the pull request (or merge `{entry.BaseBranch}` into the branch), then queue it again."
           + (detail is { Length: > 0 } text ? $"\n\nGitHub: {text}" : string.Empty);

    // ═══ Step 4: checks (O12, D9, D41) ══════════════════════════════════════════════════════════════

    private async Task EnterAwaitingChecksAsync(Context context, MergeQueueEntry entry, string why, CancellationToken ct)
    {
        Transition(entry, EMergeQueueEntryState.AwaitingChecks, why);
        await ScheduleAsync(entry, EMergeQueueStep.PollChecks, ChecksPollInterval, ct);
        await ScheduleAsync(entry, EMergeQueueStep.Timeout, TimeSpan.FromMinutes(entry.Config.ChecksTimeoutMinutes), ct);
        await PublishCheckAsync(context, entry, ct);
        await EvaluateChecksAsync(context, entry, fromPoll: false, ct);
    }

    private async Task EvaluateChecksAsync(Context context, MergeQueueEntry entry, bool fromPoll, CancellationToken ct)
    {
        var state = await context.Queue.GetCheckStateAsync(context.Repository, entry.FinalSha!, ct);
        var gate = CheckGate.Evaluate(state, entry.Config.CheckExclusions, entry.RequiredChecks, entry.ExpectedChecks);

        switch (gate.Verdict)
        {
            case ECheckVerdict.Passed:
                Transition(entry, EMergeQueueEntryState.Merging, $"checks passed on {Short(entry.FinalSha)}");
                await ScheduleAsync(entry, EMergeQueueStep.Merge, null, ct);
                await PublishCheckAsync(context, entry, ct);
                return;

            case ECheckVerdict.Failed when entry.CheckRetries < entry.Config.RetryFlaky:
                // D9: one more chance for each failed check run; a commit status cannot be re-run.
                var rerun = 0;
                foreach (var run in state.CheckRuns.Where(r => gate.Failed.Contains(r.Name) && CheckGate.ReadRun(r.Status, r.Conclusion) == ECheckVerdict.Failed))
                    if (await context.Queue.RerunCheckAsync(context.Repository, run, ct))
                        rerun++;
                if (rerun > 0)
                {
                    entry.CheckRetries++;
                    entry.Note(Now, $"re-running {rerun} failed check(s): {string.Join(", ", gate.Failed)}");
                    if (fromPoll) await ScheduleAsync(entry, EMergeQueueStep.PollChecks, ChecksPollInterval, ct);
                    return;
                }
                goto case ECheckVerdict.Failed;

            case ECheckVerdict.Failed:
                await EvictAsync(context, entry, "checks-failed",
                    $"Checks failed on `{Short(entry.FinalSha)}`: {string.Join(", ", gate.Failed.Select(f => $"`{f}`"))}.", ct: ct);
                return;

            default:
                if (fromPoll) await ScheduleAsync(entry, EMergeQueueStep.PollChecks, ChecksPollInterval, ct);
                return;
        }
    }

    private async Task TimeoutAsync(Context context, MergeQueueEntry entry, CancellationToken ct)
    {
        if (entry.State == EMergeQueueEntryState.Regenerating)
        {
            await EvictAsync(context, entry, "regenerate-timeout",
                $"The regenerate run did not finish within {entry.Config.PostRebaseTimeoutMinutes} minutes (`postRebaseTimeout`).", ct: ct);
            return;
        }

        // One last look, in case the final webhook was lost; only a check that is still pending times out.
        var generation = entry.Generation;
        await EvaluateChecksAsync(context, entry, fromPoll: false, ct);
        if (entry.State == EMergeQueueEntryState.AwaitingChecks && entry.Generation == generation)
            await EvictAsync(context, entry, "checks-timeout", $"Checks on `{Short(entry.FinalSha)}` did not finish within {entry.Config.ChecksTimeoutMinutes} minutes (`checks.timeout`).", ct: ct);
    }

    // ═══ Step 5: merge (O9, D13, D17, O18) ══════════════════════════════════════════════════════════

    private async Task MergeAsync(Context context, MergeQueueEntry entry, CancellationToken ct)
    {
        var repository = context.Repository;

        // O9: the role is checked again right before the merge; a revoked role evicts.
        var role = await context.Queue.GetActorRoleAsync(repository, entry.EnqueuedBy, ct);
        if (role < EForgeRepositoryRole.Write)
        {
            await EvictAsync(context, entry, "role-revoked", $"@{entry.EnqueuedBy}, who queued this pull request, no longer has write access.", ct: ct);
            return;
        }

        // O22, like O9: the bypass role is re-read here. What it let through the dispatch is evicted once the role no longer
        // reaches it; without it, the approval floor applies again.
        var bypass = Bypasses(role, entry.Config.BypassRole);
        if (entry.WorkflowGuardBypassed && !bypass)
        {
            await EvictAsync(context, entry, "bypass-revoked",
                $"@{entry.EnqueuedBy} no longer has the role that let this pull request's `.github/` change run with the regenerate workflow. Queue it again.", ct: ct);
            return;
        }

        // O11 again (§0 D61), like O9's role: an approval withdrawn or dismissed since enqueue, or a writer's request for
        // changes, ends the run before anything is merged.
        if (!bypass && await ApprovalFloorRefusalAsync(context, entry, ct) is { } floor)
        {
            await EvictAsync(context, entry, "approval-withdrawn", $"Checked again right before the merge: {floor.Message}", ct: ct);
            return;
        }

        var pull = await ReadPullRequestAsync(context, entry, ct);
        if (pull is null) return;

        var baseSha = await context.Queue.GetBranchHeadAsync(repository, entry.BaseBranch, ct);
        if (!string.Equals(baseSha, entry.BaseSha, StringComparison.OrdinalIgnoreCase))
        {
            await RestartAsync(context, entry, $"{entry.BaseBranch} moved to {Short(baseSha)} before the merge; rebasing again.", ct);
            return;
        }

        // §0 D60: GitHub builds the merged commit from these, not from the branch's commit, so the trailers go here too.
        var request = await context.Queue.RequestMergeAsync(repository, entry.Number, entry.FinalSha!, EForgeMergeMethod.Squash, CommitTitle(entry), CommitBody(entry), ct);
        await OnMergeRequestAsync(context, entry, request, ct);
    }

    private async Task PollMergeAsync(Context context, MergeQueueEntry entry, CancellationToken ct)
    {
        if (entry.MergeRequestId is not { } requestId)
        {
            await ScheduleAsync(entry, EMergeQueueStep.Merge, null, ct);
            return;
        }
        var request = await context.Queue.GetMergeRequestStatusAsync(context.Repository, entry.Number, requestId, ct);
        await OnMergeRequestAsync(context, entry, request, ct);
    }

    private async Task OnMergeRequestAsync(Context context, MergeQueueEntry entry, ForgeMergeRequest request, CancellationToken ct)
    {
        switch (request.State)
        {
            case EForgeMergeState.Merged:
                await CompleteAsync(context, entry, request.MergeCommitSha, ct);
                break;

            // D17: a 202 is not a merge; the rules are evaluated afterwards.
            case EForgeMergeState.Pending:
                entry.MergeRequestId = request.RequestId ?? entry.MergeRequestId;
                entry.Polls++;
                if (entry.Polls > MaxMergePolls)
                {
                    await EvictAsync(context, entry, "merge-timeout", "The forge accepted the merge request but never completed it.", ct: ct);
                    return;
                }
                await ScheduleAsync(entry, EMergeQueueStep.PollMerge, TimeSpan.FromSeconds(Math.Min(120, 15 * Math.Pow(2, Math.Min(entry.Polls - 1, 3)))), ct);
                break;

            // D17: right after our own push the forge may not have caught up; one retry after a short delay.
            case EForgeMergeState.HeadModified when entry.HeadModifiedRetries < 1:
                entry.HeadModifiedRetries++;
                entry.MergeRequestId = null;
                entry.Note(Now, "the forge answered \"head branch was modified\"; retrying once");
                await ScheduleAsync(entry, EMergeQueueStep.Merge, HeadModifiedRetryDelay, ct);
                break;

            case EForgeMergeState.HeadModified:
                await EvictAsync(context, entry, "head-modified", $"The forge says the pull request's head is no longer `{Short(entry.FinalSha)}`.", ct: ct);
                break;

            default:
                await EvictAsync(context, entry, "merge-refused",
                    $"The forge refused the merge: {request.Message ?? "no reason given"}\n\nIf this branch requires approval of the most recent push or dismisses stale approvals, turn those off for branches the queue manages: the queue's own commit is the most recent push, and it evicts any push it did not make.", ct: ct);
                break;
        }
    }

    private async Task CompleteAsync(Context context, MergeQueueEntry entry, string? mergeCommitSha, CancellationToken ct)
    {
        entry.MergeCommitSha = mergeCommitSha ?? entry.MergeCommitSha;
        await EndAsync(context, entry, EMergeQueueEntryState.Merged, null, ct);
    }

    // ═══ Ending a run ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>Ends the run with a reason the check run and a comment carry; the label goes, the next entry starts.</summary>
    private async Task EvictAsync(Context context, MergeQueueEntry entry, string reason, string message, IReadOnlyList<string>? paths = null, bool permissions = false, CancellationToken ct = default)
    {
        entry.EvictionReason = reason;
        entry.EvictionPaths = [.. paths ?? []];
        if (permissions) entry.PermissionsUrl = await PermissionsUrlAsync(context, ct);
        await EndAsync(context, entry, EMergeQueueEntryState.Evicted, message, ct);
        await BestEffortAsync(() => context.Queue.PostCommentAsync(context.Repository, entry.Number,
            $"The merge queue removed this pull request: {message}" + (entry.PermissionsUrl is { } url ? $"\n\n[Accept the App's new permissions]({url})." : string.Empty)
            + $"\n\nQueue it again with `/queue` or the `{entry.Config.Label}` label.", ct), "comment");
        logger.LogInformation("Merge queue: evicted #{Number} in {Repository}: {Reason}", entry.Number, context.Repository.FullName, reason);
    }

    private async Task EndAsync(Context context, MergeQueueEntry entry, EMergeQueueEntryState state, string? message, CancellationToken ct)
    {
        if (message is not null) entry.EvictionMessage = message;
        await AbandonRunAsync(entry, ct);
        Transition(entry, state, message ?? state.ToString());
        entry.FinishedAtUtc = Now;
        context.Document.Record(Outcome(entry));

        var lane = context.Document.LaneFor(entry.BaseBranch);
        if (lane.Current == entry.Id) lane.Current = null;
        lane.Waiting.Remove(entry.Id!);

        // D5: the queue branch goes once the run is over, under a lease on the commit we left there. Only a run that
        // dispatched ever pushed one (§0 D65), so a run that did not has nothing to delete.
        if (entry.QueueBranchSha is { } pushed)
            await outbox.EnqueueAsync(session, new MergeQueueStepMessage
            {
                EntryId = entry.Id!, Run = entry.Run, Generation = entry.Generation, Step = EMergeQueueStep.Cleanup,
                QueueBranch = entry.QueueBranch, ExpectedSha = pushed,
            }, null, ct);

        await PublishCheckAsync(context, entry, ct);
        await BestEffortAsync(() => context.Queue.RemoveLabelAsync(context.Repository, entry.Number, entry.Config.Label, ct), "remove the label");
        await StartNextAsync(context, entry.BaseBranch, ct);
    }

    /// <summary>Starts the lane's next waiting entry when the lane is idle (D11), and refreshes the waiting positions.</summary>
    private async Task StartNextAsync(Context context, string baseBranch, CancellationToken ct)
    {
        var lane = context.Document.LaneFor(baseBranch);
        if (lane.Current is null && !lane.Paused)
        {
            while (lane.Waiting.Count > 0)
            {
                var id = lane.Waiting[0];
                lane.Waiting.RemoveAt(0);
                var next = await session.LoadAsync<MergeQueueEntry>(id, ct);
                if (next is null || next.State != EMergeQueueEntryState.Queued) continue;

                lane.Current = id;
                next.StartedAtUtc = Now;
                Transition(next, EMergeQueueEntryState.Preparing, "its turn");
                await ScheduleAsync(next, EMergeQueueStep.Start, null, ct);
                await PublishCheckAsync(context, next, ct);
                break;
            }
        }

        foreach (var id in lane.Waiting.Take(10).ToList())
            if (await session.LoadAsync<MergeQueueEntry>(id, ct) is { State: EMergeQueueEntryState.Queued } waiting)
                await PublishCheckAsync(context, waiting, ct);
    }

    private async Task CleanupAsync(Context context, MergeQueueEntry entry, MergeQueueStepMessage step, CancellationToken ct)
    {
        if (!writer.IsConfigured || step.QueueBranch is null) return;
        // §0 D52: a later run of this pull request may have adopted the very commit this run left there (a queue-branch push
        // that finds its own commit there answers it); the branch is that run's now.
        if (!entry.State.IsTerminal() && string.Equals(entry.QueueBranchSha, step.ExpectedSha, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Merge queue: keeping {Branch} in {Repository}: run {Run} adopted it", step.QueueBranch, context.Repository.FullName, entry.Run);
            return;
        }
        // T9: a token of its own, like every writer job; the entry has ended, so a refusal only leaves the branch behind.
        ForgePushAccess push;
        try
        {
            push = await context.Queue.GetWriterAccessAsync(context.Repository, pullRequest: null, pushesWorkflowChanges: false, ct);
        }
        catch (ForgeAccessDeniedException ex)
        {
            // Refused now, refused on every retry (an uninstalled App, a permission not accepted).
            logger.LogWarning(ex, "Merge queue: no credential to delete {Branch} in {Repository}", step.QueueBranch, context.Repository.FullName);
            return;
        }
        if (push.Access is not { } access)
        {
            logger.LogWarning("Merge queue: no credential to delete {Branch} in {Repository}: {Reason}", step.QueueBranch, context.Repository.FullName, push.RefusalReason);
            return;
        }

        // Not gated on the entry: it may already run again. The lease keeps a newer queue branch safe.
        GitWriterDeleteResult result;
        try
        {
            result = await WithAccessAsync(context, access, credential => writer.DeleteQueueBranchAsync(new GitWriterDeleteQueueBranchRequest(
                context.Repository.Provider.ToCanonicalString(), context.Repository.GitHubId.ToString(CultureInfo.InvariantCulture),
                access.CloneUrl, credential, step.QueueBranch, step.ExpectedSha), ct));
        }
        catch (GitWriterRefusedException ex)
        {
            // §0 D57: refused now, refused on every retry; the branch is left for a person (merge-queue.md §7.4).
            logger.LogWarning(ex, "Merge queue: the git writer refused to delete {Branch} in {Repository}", step.QueueBranch, context.Repository.FullName);
            return;
        }
        if (result.Outcome is GitWriterOutcomes.Busy or GitWriterOutcomes.Failed)
            throw new GitWriterUnavailableException($"The git writer answered delete with {result.Outcome}: {result.Detail}");
        logger.LogInformation("Merge queue: {Branch} in {Repository}: {Outcome}", step.QueueBranch, context.Repository.FullName, result.Outcome);
    }

    /// <summary>
    /// §0 D53: queues the cancellation of the regenerate run when the entry stops waiting for it for any reason but the
    /// run's own completion (a moved base, an eviction, a dequeue, a close, the timeout). Through the outbox, so the run is
    /// only cancelled when the state change that abandons it commits.
    /// </summary>
    private async Task AbandonRunAsync(MergeQueueEntry entry, CancellationToken ct)
    {
        if (entry.State != EMergeQueueEntryState.Regenerating || entry.DispatchRunId is not { } runId || entry.DispatchRunFinished) return;
        entry.DispatchRunFinished = true;
        await outbox.EnqueueAsync(session, new MergeQueueStepMessage
        {
            EntryId = entry.Id!, Run = entry.Run, Generation = entry.Generation, Step = EMergeQueueStep.CancelRun, RunId = runId,
        }, null, ct);
    }

    /// <summary>
    /// Cancels an abandoned regenerate run. Not gated on the entry, like <see cref="CleanupAsync"/>: the entry has moved on
    /// by design. A forge failure that may pass throws, so the messaging runtime retries the step; a refused permission is
    /// noted and dropped, since the entry no longer depends on the run.
    /// </summary>
    private async Task CancelRunAsync(Context context, MergeQueueEntry entry, MergeQueueStepMessage step, CancellationToken ct)
    {
        if (step.RunId is not { } runId) return;
        try
        {
            var cancelled = await context.Queue.CancelWorkflowRunAsync(context.Repository, runId, ct);
            entry.Note(Now, cancelled ? $"cancelled the abandoned regenerate run {runId}" : $"the abandoned regenerate run {runId} had already finished");
        }
        catch (ForgeAccessDeniedException ex)
        {
            entry.Note(Now, $"could not cancel the abandoned regenerate run {runId}: {ex.Message}");
            logger.LogWarning(ex, "Merge queue: could not cancel run {RunId} in {Repository}", runId, context.Repository.FullName);
        }
    }

    // ═══ Helpers ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs one step body: a transient failure re-runs the same step later (bounded), a refused permission evicts with the
    /// acceptance link (O17).
    /// </summary>
    private async Task GuardedAsync(Context context, MergeQueueEntry entry, EMergeQueueStep retryStep, Func<Task> body, CancellationToken ct)
    {
        try
        {
            await body();
        }
        catch (ForgeAccessDeniedException ex)
        {
            await EvictAsync(context, entry, "permission",
                $"The forge refused a call the queue needs{(ex.RequiredPermission is { } p ? $" (it asks for `{p}`)" : string.Empty)}: {ex.Message}", permissions: true, ct: ct);
        }
        catch (GitWriterRefusedException ex)
        {
            // §0 D57: the writer refuses the same request every time; retrying would only delay this answer.
            await EvictAsync(context, entry, "writer-refused",
                $"The queue's git writer refused the {ex.Job} request: `{ex.Field}` must be {ex.Rule}. Sending it again cannot change that, so the run ends here.", ct: ct);
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            entry.TransientFailures++;
            entry.LastError = ex.Message;
            // §0 D58: the first failure and MaxTransientRetries retries; the failure after the last retry evicts.
            if (entry.TransientFailures > MaxTransientRetries)
            {
                await EvictAsync(context, entry, "error",
                    $"The queue gave up after {entry.TransientFailures} failed attempts in a row (the first and {MaxTransientRetries} retries): {ex.Message}", ct: ct);
                return;
            }
            var delay = TimeSpan.FromSeconds(Math.Min(600, 15 * Math.Pow(2, entry.TransientFailures - 1)));
            logger.LogWarning(ex, "Merge queue: {Step} of {Entry} failed ({Failures}/{Max}); retrying in {Delay}", retryStep, entry.Id, entry.TransientFailures, MaxTransientRetries + 1, delay);
            await ScheduleAsync(entry, retryStep, delay, ct);
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken ct)
        => ex is ForgeRequestException or GitWriterUnavailableException or HttpRequestException or TimeoutException
           || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    /// <summary>Reads the pull request and ends the run when it is no longer the one that was queued. Null: ended.</summary>
    /// <param name="headJudgedByWriter">
    /// Publish only (§0 D52): a head the queue does not know is left to the writer, which recognises the commit an
    /// earlier attempt of the same publish pushed and answers "branch moved" for anything else.
    /// </param>
    private async Task<ForgePullRequest?> ReadPullRequestAsync(Context context, MergeQueueEntry entry, CancellationToken ct, bool headJudgedByWriter = false)
    {
        var pull = await context.Forge.GetPullRequestAsync(context.Repository, entry.Number, ct)
            ?? throw new ForgeRequestException($"Pull request #{entry.Number} of {context.Repository.FullName} could not be read.", null);
        if (!pull.IsOpen)
        {
            await EndAsync(context, entry, EMergeQueueEntryState.Dequeued, "The pull request was closed.", ct);
            return null;
        }
        if (!headJudgedByWriter && !string.Equals(pull.HeadSha, entry.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase) && !entry.KnownShas.Contains(pull.HeadSha, StringComparer.OrdinalIgnoreCase))
        {
            await EvictAsync(context, entry, "author-push",
                $"The pull request's branch moved to `{Short(pull.HeadSha)}`, a commit the queue did not write. The queue merges only the commit that was queued: queue it again to merge the new one.", ct: ct);
            return null;
        }
        if (pull.BaseRef != entry.BaseBranch)
        {
            await EvictAsync(context, entry, "retargeted", $"The pull request now targets `{pull.BaseRef}` instead of `{entry.BaseBranch}`; queue it again.", ct: ct);
            return null;
        }
        return pull;
    }

    /// <summary>
    /// One writer job's access (T9, §0 D59): the base clone URL, where to push (O3: a fork's pull request pushes to the
    /// fork) and a token minted for this job alone. Null: evicted with the forge's reason.
    /// </summary>
    /// <param name="pullRequest">The pull request whose branch the job pushes; null for a job on the queue branch only.</param>
    private async Task<ForgeWriterAccess?> WriterAccessAsync(Context context, MergeQueueEntry entry, ForgePullRequest? pullRequest, bool pushesWorkflowChanges, CancellationToken ct)
    {
        var push = await context.Queue.GetWriterAccessAsync(context.Repository, pullRequest, pushesWorkflowChanges, ct);
        if (push.Access is { } access) return access;
        await EvictAsync(context, entry, "push-refused", push.RefusalReason ?? "The forge gave no credential to push the rebased commit.", ct: ct);
        return null;
    }

    /// <summary>
    /// Runs one writer job with its own credential, then ends the credential (T9): whatever the job answered or threw, the
    /// token dies with it instead of living out its hour.
    /// </summary>
    private static async Task<T> WithAccessAsync<T>(Context context, ForgeWriterAccess access, Func<SidecarCredential, Task<T>> job)
    {
        try
        {
            return await job(new SidecarCredential(access.Username, access.Password));
        }
        finally
        {
            // Bounded inside, and never thrown from: the job's own outcome is what the step acts on.
            await context.Queue.RevokeWriterAccessAsync(context.Repository, access, CancellationToken.None);
        }
    }

    /// <summary>
    /// §0 D59, D71: whether a publish's token asks for the workflow permission. The published commit is the prepared one (a
    /// patch never touches <c>.github</c>, T4). A same-repository publish: when the pull request's own change touches
    /// <c>.github/workflows/**</c>, against the base (O21 A lets it through when nothing is dispatched); the base's workflow
    /// files are on the base branch already. A fork's publish never: GitHub evaluates the permission against the fork,
    /// where the base's installation holds none, and refuses a workflow file the fork has on no branch whatever the token
    /// asks for (§0 D69, measured live). Asking would only widen the token (T9). Before D69, a fork's asked when the base
    /// changed its workflows (<see cref="MergeQueueEntry.BaseChangedWorkflows"/>) or its own change touched them; such a
    /// refusal now ends the entry <see cref="ForkWorkflowUpdateNeeded"/>.
    /// </summary>
    private static bool PublishPushesWorkflowChanges(MergeQueueEntry entry)
        => !entry.IsFork && entry.WorkflowPaths.Any(MergeQueueWorkflowGuard.IsWorkflowFile);

    /// <summary>
    /// O22: whether <paramref name="role"/>, the enqueuer's as the forge reports it now, reaches the bypass role. Asked at
    /// enqueue (the approval floor), when a prepare decides the dispatch (a same-repository <c>.github/**</c> change) and
    /// right before the merge, like O9's role. The check gate, D12's workflow identity, T4's patch validation, T3's lease
    /// and pinning, O9's own <c>write</c> minimum and a fork's <c>.github/**</c> change that needs the dispatch (O25) are
    /// never bypassed.
    /// </summary>
    private static bool Bypasses(EForgeRepositoryRole role, EMergeQueueBypassRole bypassRole) => bypassRole switch
    {
        EMergeQueueBypassRole.Maintain => role >= EForgeRepositoryRole.Maintain,
        EMergeQueueBypassRole.Admin => role >= EForgeRepositoryRole.Admin,
        _ => false,
    };

    private static string PullHeadRef(MergeQueueEntry entry) => $"refs/pull/{entry.Number.ToString(CultureInfo.InvariantCulture)}/head";

    private static string CommitTitle(MergeQueueEntry entry)
        => $"{(string.IsNullOrWhiteSpace(entry.Title) ? $"Pull request {entry.Number.ToString(CultureInfo.InvariantCulture)}" : entry.Title.Trim())} (#{entry.Number.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>The squashed commit's message: the title, then the co-author trailers (§0 D60).</summary>
    private static string CommitMessage(MergeQueueEntry entry)
        => CommitBody(entry) is { } body ? $"{CommitTitle(entry)}\n\n{body}" : CommitTitle(entry);

    /// <summary>The merge's <c>commit_message</c>: the co-author trailers, or nothing (§0 D60).</summary>
    private static string? CommitBody(MergeQueueEntry entry)
        => entry.CoAuthors is { Count: > 0 } coAuthors ? string.Join('\n', coAuthors) : null;

    /// <summary>The most co-authors one squashed commit credits (§0 D60). GitHub lists 250 commits at most, but a single
    /// commit message may credit any number, and the message must stay within the writer's limit.</summary>
    internal const int MaxCoAuthors = 100;

    /// <summary>
    /// §0 D60: the <c>Co-authored-by:</c> trailers of the squashed commit. Credited are the authors of the pull request's
    /// commits, and the people those commits credit in <c>Co-authored-by:</c> trailers of their own, as GitHub's squash
    /// does by keeping every commit message. That also carries the credits over when a pull request whose branch already
    /// holds the queue's squash is queued again. Each is credited once, by email, in commit order. Excluded are the author
    /// (GitHub credits them as the commit's author), bots, the queue itself, and any identity the git writer would refuse.
    /// </summary>
    internal static IReadOnlyList<string> CoAuthorTrailers(IReadOnlyList<ForgePullRequestCommit> commits, string? authorLogin, string queueEmail)
    {
        // The author's addresses: those of the commits GitHub attributes to them, and GitHub's noreply forms of the login.
        var authorEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (authorLogin is not null)
        {
            authorEmails.Add($"{authorLogin}@users.noreply.github.com");
            foreach (var commit in commits)
                if (string.Equals(commit.AuthorLogin, authorLogin, StringComparison.OrdinalIgnoreCase) && commit.AuthorEmail is { } email)
                    authorEmails.Add(email.Trim());
        }
        bool IsAuthor(string? login, string email)
            => authorLogin is not null && (string.Equals(login, authorLogin, StringComparison.OrdinalIgnoreCase) || authorEmails.Contains(email)
                || email.EndsWith($"+{authorLogin}@users.noreply.github.com", StringComparison.OrdinalIgnoreCase));
        static bool IsBot(string? login, string name, string email)
            => (login?.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) ?? false)
               || name.Contains("[bot]", StringComparison.OrdinalIgnoreCase) || email.Contains("[bot]", StringComparison.OrdinalIgnoreCase);

        var credited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { queueEmail };
        var trailers = new List<string>();
        void Credit(string? name, string? email, string? login, bool isBot)
        {
            name = name?.Trim();
            email = email?.Trim();
            if (trailers.Count >= MaxCoAuthors || isBot || !MergeQueueWireRules.IsIdentity(name, email)
                || IsBot(login, name!, email!) || IsAuthor(login, email!) || !credited.Add(email!))
                return;
            trailers.Add($"Co-authored-by: {name} <{email}>");
        }

        foreach (var commit in commits)
        {
            Credit(commit.AuthorName, commit.AuthorEmail, commit.AuthorLogin, commit.AuthorIsBot);
            foreach (Match trailer in CoAuthoredBy().Matches(commit.Message))
                Credit(trailer.Groups["name"].Value, trailer.Groups["email"].Value, null, false);
        }
        return trailers;
    }

    /// <summary>A <c>Co-authored-by: Name &lt;email&gt;</c> line, as GitHub reads it in a commit message.</summary>
    [GeneratedRegex(@"^co-authored-by:[ \t]*(?<name>[^<>\r\n]+?)[ \t]*<(?<email>[^<>\s]+)>[ \t]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CoAuthoredBy();

    /// <summary>The ended run as the repository page lists it (M6, §0 D55).</summary>
    private static MergeQueueOutcome Outcome(MergeQueueEntry entry) => new()
    {
        Number = entry.Number,
        Title = entry.Title,
        BaseBranch = entry.BaseBranch,
        Run = entry.Run,
        State = entry.State,
        Reason = entry.State == EMergeQueueEntryState.Merged ? null : entry.EvictionReason,
        Message = entry.EvictionMessage is { Length: > MergeQueueOutcome.MaxMessageLength } text
            ? text[..(MergeQueueOutcome.MaxMessageLength - 1)] + "…"
            : entry.EvictionMessage,
        EnqueuedBy = entry.EnqueuedBy,
        MergeCommitSha = entry.MergeCommitSha,
        FinishedAtUtc = entry.FinishedAtUtc ?? entry.StateChangedAtUtc,
    };

    private static void Remember(MergeQueueEntry entry, string sha)
    {
        if (!entry.KnownShas.Contains(sha, StringComparer.OrdinalIgnoreCase)) entry.KnownShas.Add(sha);
    }

    private void Transition(MergeQueueEntry entry, EMergeQueueEntryState state, string why)
    {
        entry.State = state;
        entry.Generation++;
        entry.TransientFailures = 0;
        entry.Polls = 0;
        entry.StateChangedAtUtc = Now;
        entry.Note(Now, why);
    }

    private Task ScheduleAsync(MergeQueueEntry entry, EMergeQueueStep step, TimeSpan? delay, CancellationToken ct)
        => outbox.EnqueueAsync(session, new MergeQueueStepMessage
        {
            EntryId = entry.Id!,
            Run = entry.Run,
            Generation = entry.Generation,
            Step = step,
        }, delay is { } wait ? new BroadcastOptions { Delay = wait } : null, ct);

    private async Task<Context?> LoadContextAsync(string repositoryId, bool create, CancellationToken ct)
    {
        var repository = await session.LoadAsync<Repository>(repositoryId, ct);
        if (repository?.Id is null) return null;
        var queue = forgeQueues.FirstOrDefault(q => q.Provider == repository.Provider);
        if (queue is null)
        {
            logger.LogDebug("Merge queue: {Provider} has no merge queue support ({Repository})", repository.Provider, repository.FullName);
            return null;
        }

        var id = QueueDocument.DocumentId(repository.Id);
        var document = await session.LoadAsync<QueueDocument>(id, ct);
        if (document is null)
        {
            if (!create) return null;
            document = new QueueDocument { Id = id, Repository = repository.Id };
            await session.StoreAsync(document, id, ct);
        }
        var account = repository.Account is null ? null : await session.LoadAsync<Account>(repository.Account, ct);
        return new Context(repository, account, forges.For(repository), queue, document);
    }

    private async Task<MergeQueueEntry?> LoadActiveAsync(string repositoryId, int number, CancellationToken ct)
        => await session.LoadAsync<MergeQueueEntry>(MergeQueueEntry.DocumentId(repositoryId, number), ct) is { } entry && !entry.State.IsTerminal() ? entry : null;

    private async Task<IReadOnlyList<MergeQueueEntry>> LoadCurrentEntriesAsync(Context context, CancellationToken ct)
    {
        var ids = context.Document.Lanes.Select(l => l.Current).OfType<string>().ToList();
        if (ids.Count == 0) return [];
        var loaded = await session.LoadAsync<MergeQueueEntry>(ids, ct);
        return [.. loaded.Values.OfType<MergeQueueEntry>().Where(e => !e.State.IsTerminal())];
    }

    /// <summary>Creates or updates the queue's check run on the head the pull request should have now. Never throws.</summary>
    private async Task PublishCheckAsync(Context context, MergeQueueEntry entry, CancellationToken ct)
    {
        var sha = entry.ExpectedHeadSha;
        if (string.IsNullOrEmpty(sha)) return;
        var position = context.Document.Lanes.FirstOrDefault(l => l.BaseBranch == entry.BaseBranch)?.PositionOf(entry.Id!);
        var check = MergeQueueCheckRenderer.Render(entry, position, DetailsUrl(context.Repository));
        var existing = string.Equals(entry.QueueCheckSha, sha, StringComparison.OrdinalIgnoreCase) ? entry.QueueCheckId : null;
        try
        {
            entry.QueueCheckId = await context.Queue.PublishQueueCheckAsync(context.Repository, sha, check, existing, ct);
            entry.QueueCheckSha = sha;
        }
        catch (Exception ex) when (ex is ForgeAccessDeniedException || IsTransient(ex, ct))
        {
            logger.LogWarning(ex, "Merge queue: could not publish the check run for #{Number} in {Repository}", entry.Number, context.Repository.FullName);
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
            logger.LogWarning(ex, "Merge queue: could not {What}", what);
        }
    }

    /// <summary>O17: the installation's "accept the new permissions" page, when the forge can name it.</summary>
    private async Task<string?> PermissionsUrlAsync(Context context, CancellationToken ct)
    {
        if (context.Account is null) return null;
        var source = permissionSources.FirstOrDefault(p => p.Provider == context.Repository.Provider);
        if (source is null) return null;
        try
        {
            return (await source.GetPendingAsync(context.Account, ct))?.HtmlUrl;
        }
        catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException or HttpRequestException)
        {
            logger.LogWarning(ex, "Merge queue: could not read the pending permissions of {Account}", context.Account.Login);
            return null;
        }
    }

    /// <summary>The repository page, where "Details" on the check run leads; null unless the base URL is https.</summary>
    private string? DetailsUrl(Repository repository)
    {
        var baseUrl = configuration["Coverage:BaseUrl"];
        if (baseUrl is null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) || root.Scheme != Uri.UriSchemeHttps) return null;
        return $"{baseUrl.TrimEnd('/')}/{repository.Provider.ToCanonicalString()}/r/{Uri.EscapeDataString(repository.OwnerLogin)}/{Uri.EscapeDataString(repository.Name)}";
    }

    private static string Short(string? sha) => sha is { Length: > 12 } ? sha[..12] : sha ?? "?";
}

/// <summary>The git writer was busy or failed in a way a later attempt may not; the saga retries the step.</summary>
public sealed class GitWriterUnavailableException(string message) : Exception(message);
