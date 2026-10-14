using CodeCoverage.LookupReferences;

namespace CodeCoverage.Entities;

/// <summary>
/// One pull request's run through the merge queue (merge-queue PRD §5.1, §5.5, O20): the saga's state.
/// </summary>
/// <remarks>
/// <para>
/// <b>One document per pull request</b>, <c>MergeQueueEntries/{provider}/{repositoryId}/{number}</c>, reused when the
/// pull request is queued again: a webhook can then find it by a point load, and a replayed <c>/queue</c> lands on the
/// same document instead of creating a second entry. <see cref="Run"/> counts the attempts.
/// </para>
/// <para>
/// <b>Idempotency.</b> Every step message carries the <see cref="Run"/> and <see cref="Generation"/> it was scheduled
/// for, and every state change increments <see cref="Generation"/>, so a step that arrives late, twice or after the
/// entry moved on is a no-op. Signals (webhooks) are evaluated against the current state, so a replay finds nothing to
/// do.
/// </para>
/// </remarks>
public class MergeQueueEntry
{
    public string? Id { get; set; }

    /// <summary>The repository document id.</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>The base branch, which names the lane.</summary>
    public string BaseBranch { get; set; } = string.Empty;

    public int Number { get; set; }

    /// <summary>1 for the first time this pull request was queued, then one more per re-queue.</summary>
    public int Run { get; set; }

    public EMergeQueueEntryState State { get; set; }

    /// <summary>Incremented on every state change; a step scheduled for an older generation is ignored.</summary>
    public int Generation { get; set; }

    // ── The authorisation (O9, O10, T3) ─────────────────────────────────────────────────────────────

    /// <summary>The head the actor authorised, pinned at enqueue. Prepare fetches exactly this commit (T3).</summary>
    public string PinnedHeadSha { get; set; } = string.Empty;

    /// <summary>The forge login that queued it: the signed webhook's sender or the signed-in user's linked login (T1).</summary>
    public string EnqueuedBy { get; set; } = string.Empty;

    public EMergeQueueCommandSource EnqueuedVia { get; set; }

    /// <summary>The pull request's head branch name (in the fork for a fork's pull request).</summary>
    public string HeadBranch { get; set; } = string.Empty;

    public bool IsFork { get; set; }

    public string? AuthorLogin { get; set; }

    public string? Title { get; set; }

    /// <summary>
    /// The <c>Co-authored-by:</c> trailers of the squashed commit and of the merge (§0 D60), read once when the run starts,
    /// while the pull request's branch still holds its own commits. Null until then.
    /// </summary>
    public List<string>? CoAuthors { get; set; }

    /// <summary>What the pull request's branch should hold now: the pinned head, then each commit the queue published.</summary>
    public string ExpectedHeadSha { get; set; } = string.Empty;

    /// <summary>
    /// Every commit this run put on the pull request's branch or the queue branch, plus the pinned head (§5.5). A push
    /// whose new head is not one of these is an author push, and evicts (T3).
    /// </summary>
    public List<string> KnownShas { get; set; } = [];

    // ── The steps ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The base branch's head this attempt was built on.</summary>
    public string? BaseSha { get; set; }

    /// <summary>The squashed, rebased commit the prepare built (§5.1 step 1). The writer pushes it nowhere until a dispatch
    /// needs it on the queue branch (§0 D65); the publish rebuilds it.</summary>
    public string? PreparedSha { get; set; }

    /// <summary>
    /// The commit this run pushed to the queue branch, or null while the queue believes the branch does not exist (§0 D65).
    /// Set only by the queue-branch push before a dispatch, so a run that never dispatches never creates the branch, and
    /// the cleanup deletes only a branch that was pushed, under a lease on this commit (D5).
    /// </summary>
    public string? QueueBranchSha { get; set; }

    /// <summary>The paths the resolver took from the base side; non-empty means regeneration is due.</summary>
    public List<string> ResolvedPaths { get; set; } = [];

    /// <summary>
    /// The prepared commit's <c>.github/workflows</c> differs from the pinned head's, as the prepare saw it (§0 D59, D67):
    /// the base changed workflow files since the head. Publishing to a fork then needs the forge's workflow permission,
    /// because the fork's branch holds the head and lacks the base's newer workflow files.
    /// </summary>
    public bool BaseChangedWorkflows { get; set; }

    /// <summary>
    /// The <c>.github/**</c> paths where the prepared commit differs from its base (the pull request's own change, or one
    /// a rename on the base carries there), as the prepare reported them. Allowed only when nothing is dispatched, or
    /// through O22's bypass role for a same-repository pull request (§0 D63, O25).
    /// </summary>
    public List<string> WorkflowPaths { get; set; } = [];

    /// <summary>
    /// The regenerate workflow was dispatched with a <c>.github/**</c> change because the enqueuer had the bypass role
    /// (O22). Re-checked before the merge: if the role no longer reaches it, the entry is evicted.
    /// </summary>
    public bool WorkflowGuardBypassed { get; set; }

    /// <summary>The regenerate workflow run (D14).</summary>
    public long? DispatchRunId { get; set; }

    public string? DispatchRunUrl { get; set; }

    /// <summary>
    /// The regenerate run reported its completion, vanished, or its cancellation was queued. While false, an entry that
    /// leaves <see cref="EMergeQueueEntryState.Regenerating"/> for any other reason cancels the run, which would otherwise
    /// keep spending the repository's CI minutes (merge-queue PRD §0 D53).
    /// </summary>
    public bool DispatchRunFinished { get; set; }

    /// <summary>The commit whose checks the queue waits for and that it merges.</summary>
    public string? FinalSha { get; set; }

    /// <summary>
    /// Whether <see cref="FinalSha"/> is the pinned head itself (O20 case 1: it already contains the latest base), so
    /// nothing was published.
    /// </summary>
    public bool DirectMerge { get; set; }

    /// <summary>Checks a ruleset requires on the base branch (never excluded, O12).</summary>
    public List<string> RequiredChecks { get; set; } = [];

    /// <summary>O12's expected minimum: the checks that reported on the head pinned at enqueue (D45).</summary>
    public List<string> ExpectedChecks { get; set; } = [];

    /// <summary>Failed checks re-run so far (D9).</summary>
    public int CheckRetries { get; set; }

    /// <summary>The forge's merge request id (D13, D17).</summary>
    public string? MergeRequestId { get; set; }

    public string? MergeCommitSha { get; set; }

    /// <summary>"Head branch was modified" retries spent (D17: one).</summary>
    public int HeadModifiedRetries { get; set; }

    /// <summary>Times this run went back to prepare (base moved, queue branch out of step); bounded.</summary>
    public int PrepareAttempts { get; set; }

    /// <summary>Consecutive transient failures of the current step (forge 5xx, writer busy); reset on every state change.</summary>
    public int TransientFailures { get; set; }

    public string? LastError { get; set; }

    /// <summary>Polls of the current wait so far; reset on every state change.</summary>
    public int Polls { get; set; }

    // ── The outcome ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A stable code: <c>conflict</c>, <c>author-push</c>, <c>checks-failed</c>, …</summary>
    public string? EvictionReason { get; set; }

    /// <summary>What the check run and the comment say.</summary>
    public string? EvictionMessage { get; set; }

    public List<string> EvictionPaths { get; set; } = [];

    /// <summary>The installation's "accept the new permissions" page, when a call failed for lack of one (O17).</summary>
    public string? PermissionsUrl { get; set; }

    // ── What the queue ran under ─────────────────────────────────────────────────────────────────────

    /// <summary>The effective configuration, read at enqueue, each value with its source (O15).</summary>
    public MergeQueueConfigSnapshot Config { get; set; } = new();

    /// <summary>Things that will not stop the queue but that the owner should change (O18).</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>The commit the queue's own check run is on, and its id there.</summary>
    public string? QueueCheckSha { get; set; }

    public long? QueueCheckId { get; set; }

    // ── Time ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When this run was queued. Also the author and committer date of every commit the run writes (§0 D65), to the whole
    /// second: the writer rebuilds the prepared commit from the request, so the date must not change within a run.
    /// </summary>
    public DateTime EnqueuedAtUtc { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime StateChangedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>The run's steps, oldest first, capped at <see cref="MaxHistory"/>.</summary>
    public List<MergeQueueEntryEvent> History { get; set; } = [];

    /// <summary>Earlier runs of this pull request, oldest first, capped at <see cref="MaxPastRuns"/>.</summary>
    public List<MergeQueuePastRun> PastRuns { get; set; } = [];

    public const int MaxHistory = 50;
    public const int MaxPastRuns = 10;

    public void Note(DateTime atUtc, string text)
    {
        History.Add(new MergeQueueEntryEvent { AtUtc = atUtc, State = State, Text = text });
        if (History.Count > MaxHistory) History.RemoveAt(0);
    }

    /// <summary><c>MergeQueueEntries/{provider}/{repositoryId}/{number}</c>.</summary>
    public static string DocumentId(string repositoryId, int number)
        => $"MergeQueueEntries/{MergeQueue.RepositorySuffix(repositoryId)}/{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>The queue branch of this entry (D5): <c>mq/{base}/pr-{n}</c>.</summary>
    public string QueueBranch => $"mq/{BaseBranch}/pr-{Number.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}

public enum EMergeQueueEntryState
{
    /// <summary>Waiting for its turn in the lane.</summary>
    Queued,

    /// <summary>The writer is (re)building the squashed commit on the latest base.</summary>
    Preparing,

    /// <summary>The regenerate workflow runs on the queue branch.</summary>
    Regenerating,

    /// <summary>The final commit is being written to the pull request's branch.</summary>
    Publishing,

    /// <summary>Waiting for every counted check on the final commit (O12).</summary>
    AwaitingChecks,

    /// <summary>A merge request is pending at the forge (D17).</summary>
    Merging,

    Merged,

    /// <summary>Removed by the queue, with a reason.</summary>
    Evicted,

    /// <summary>Removed by a person (<c>/dequeue</c>, label removed) or by the pull request closing.</summary>
    Dequeued,

    /// <summary>The enqueue itself was refused (role, draft, approvals, configuration, rules).</summary>
    Refused,
}

public static class MergeQueueEntryStates
{
    public static bool IsTerminal(this EMergeQueueEntryState state)
        => state is EMergeQueueEntryState.Merged or EMergeQueueEntryState.Evicted or EMergeQueueEntryState.Dequeued or EMergeQueueEntryState.Refused;
}

/// <summary>How a command reached the queue (O8). Never who sent it: that is the actor.</summary>
public enum EMergeQueueCommandSource
{
    Label,
    Comment,
    Button,
}

public sealed class MergeQueueEntryEvent
{
    public DateTime AtUtc { get; set; }
    public EMergeQueueEntryState State { get; set; }
    public string Text { get; set; } = string.Empty;
}

public sealed class MergeQueuePastRun
{
    public int Run { get; set; }
    public EMergeQueueEntryState State { get; set; }
    public string? Reason { get; set; }
    public string? PinnedHeadSha { get; set; }
    public string? MergeCommitSha { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
}

/// <summary>
/// The configuration one run used: the file's behaviour (O15: <c>coverage.yml</c> on the default branch, O16) and the
/// owner's trust policy (approval floor O11, check exclusions O12), with where each value came from.
/// </summary>
public sealed class MergeQueueConfigSnapshot
{
    /// <summary>The branch and commit <c>coverage.yml</c> was read from.</summary>
    public string? ConfigRef { get; set; }

    public List<string> Branches { get; set; } = [];

    public string Label { get; set; } = "merge-queue";

    public string? PostRebaseActionName { get; set; }

    public int PostRebaseTimeoutMinutes { get; set; } = 20;

    public EPostRebaseWhen PostRebaseWhen { get; set; }

    public List<string> RegeneratedPaths { get; set; } = [];

    public List<MergeQueueAutoResolveRule> AutoResolve { get; set; } = [];

    /// <summary>Failed checks re-run once each before evicting (D9).</summary>
    public int RetryFlaky { get; set; } = 1;

    public int ChecksTimeoutMinutes { get; set; } = 60;

    public EMergeQueueApprovalFloor ApprovalFloor { get; set; }

    public List<string> CheckExclusions { get; set; } = [];

    /// <summary>The role from which the enqueuer bypasses the queue's own rules (O22); re-checked before the merge.</summary>
    public EMergeQueueBypassRole BypassRole { get; set; }

    /// <summary>Every value above as displayed, with its source: <c>coverage.yml</c>, <c>default</c>, <c>organization</c>, <c>repository</c>.</summary>
    public List<MergeQueueConfigValue> Values { get; set; } = [];
}

public sealed class MergeQueueConfigValue
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

/// <summary>One <c>autoResolve</c> entry: take the base side of a conflict in <see cref="Path"/> (O1, O2, O19).</summary>
public sealed class MergeQueueAutoResolveRule
{
    public string Path { get; set; } = string.Empty;

    /// <summary>Top-level JSON keys whose lines may conflict (O2); empty means any line of the file (O19).</summary>
    public List<string> Keys { get; set; } = [];
}

/// <summary>When the regenerate workflow runs (§5.2).</summary>
public enum EPostRebaseWhen
{
    /// <summary>Only when the resolver took the base side of a conflict.</summary>
    AutoResolved,
    Always,
    Never,
}
