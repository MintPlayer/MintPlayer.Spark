using CodeCoverage.Entities;

namespace CodeCoverage.Forge;

/// <summary>
/// What the merge queue needs from a forge (merge-queue PRD §5.4, M4): roles, branch rules, check
/// state, the regenerate workflow, the merge itself and the queue's own feedback.
/// </summary>
/// <remarks>
/// <para>
/// <b>A narrow interface of its own</b>, beside <see cref="IForgeIntegration"/> rather than inside it, the
/// same way <see cref="IForgeInstallationPermissions"/> is: the queue is one feature, and its fifteen
/// members would double the width of the seam every other caller has to read. A forge supports it when its
/// <see cref="IForgeIntegration.Capabilities"/> lists <see cref="EForgeCapability.MergeQueue"/>, and then it
/// must also register exactly one implementation of this interface for its <see cref="Provider"/>; the
/// conformance test holds both directions. GitHub only in this PR (O7); GitLab and Bitbucket follow under #422.
/// </para>
/// <para>
/// <b>No forge type appears in any signature</b>, and no credential either, except
/// <see cref="GetPushAccessAsync"/>, which exists to hand one to the git-writer sidecar.
/// </para>
/// <para>
/// <b>Errors.</b> A read that finds nothing answers its documented empty value (null, <c>None</c>, an empty
/// list). A refusal for lack of permission throws <see cref="ForgeAccessDeniedException"/>, carrying the
/// permission the forge says it needed when it says one (O17: the queue check run links to the
/// acceptance page). Anything else the forge answers throws <see cref="ForgeRequestException"/>, which the
/// saga treats as retryable. Outcomes the queue acts on (a stale head on merge, a workflow that cannot be
/// dispatched, an oversized artifact) are typed results, never exceptions.
/// </para>
/// </remarks>
public interface IForgeMergeQueue
{
    /// <summary>The single forge this instance speaks for.</summary>
    EForgeProvider Provider { get; }

    /// <summary>
    /// The repository role of <paramref name="login"/> (O9: enqueueing needs at least
    /// <see cref="EForgeRepositoryRole.Write"/>, re-checked right before the merge).
    /// <see cref="EForgeRepositoryRole.None"/> for an unknown or malformed login.
    /// </summary>
    Task<EForgeRepositoryRole> GetActorRoleAsync(Repository repository, string login, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every rule that applies to <paramref name="branch"/>: rulesets (with whether this App can bypass each,
    /// D16/T7) and classic branch protection as far as it is readable.
    /// </summary>
    Task<ForgeBranchRules> GetBranchRulesAsync(Repository repository, string branch, CancellationToken cancellationToken = default);

    /// <summary>
    /// The commit <paramref name="branch"/> points at now, or null when there is no such branch (M5: O20's "contains the
    /// latest base", and the base commit whose checks are the expected minimum, O12).
    /// </summary>
    Task<string?> GetBranchHeadAsync(Repository repository, string branch, CancellationToken cancellationToken = default);

    /// <summary>All check runs, commit statuses and check suites on <paramref name="sha"/> (O12; see <see cref="CheckGate"/>).</summary>
    Task<ForgeCheckState> GetCheckStateAsync(Repository repository, string sha, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the forge to run a failed check again (D9's <c>retryFlaky</c>). False when the forge cannot re-run this one
    /// (another App's check that does not support it, a run too old to re-run); the queue then evicts.
    /// </summary>
    Task<bool> RerunCheckAsync(Repository repository, ForgeCheckRun checkRun, CancellationToken cancellationToken = default);

    /// <summary>
    /// The names of every check run and commit status on <paramref name="sha"/>, ordinal-sorted and distinct:
    /// the default expected-minimum set when <paramref name="sha"/> is the base branch's latest commit (O12).
    /// </summary>
    Task<IReadOnlyList<string>> GetCheckNamesOnCommitAsync(Repository repository, string sha, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatches <paramref name="workflowFile"/> (a file name under <c>.github/workflows</c>) on
    /// <paramref name="branch"/> and returns the run it started (D14).
    /// </summary>
    /// <remarks>
    /// ⚠️ The run executes the workflow file <b>as it is on <paramref name="branch"/></b>, not the default
    /// branch's (D12). The caller asserts the file's blob first (<see cref="GetFileBlobShaAsync"/>).
    /// </remarks>
    Task<ForgeWorkflowDispatch> DispatchWorkflowAsync(
        Repository repository,
        string workflowFile,
        string branch,
        IReadOnlyDictionary<string, string> inputs,
        CancellationToken cancellationToken = default);

    /// <summary>One workflow run, or null when it does not exist. Its <see cref="ForgeWorkflowRun.HeadSha"/> is the commit it built (D14).</summary>
    Task<ForgeWorkflowRun?> GetWorkflowRunAsync(Repository repository, long runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the forge to cancel workflow run <paramref name="runId"/>: a regenerate run the queue no longer waits for
    /// would otherwise keep spending the repository's CI minutes (merge-queue PRD D51 (5), D53). True when the forge
    /// accepted the cancellation; false when the run had already finished or does not exist, which leaves the caller
    /// with nothing to do either way.
    /// </summary>
    Task<bool> CancelWorkflowRunAsync(Repository repository, long runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent runs of <paramref name="workflowFile"/> on <paramref name="branch"/>, newest first (one page). The
    /// queue reads it before dispatching, to find a run an earlier dispatch started whose answer never arrived (§0 D53).
    /// </summary>
    Task<IReadOnlyList<ForgeWorkflowRun>> ListWorkflowRunsAsync(Repository repository, string workflowFile, string branch, CancellationToken cancellationToken = default);

    /// <summary>
    /// The single file inside the run's artifact <paramref name="artifactName"/>, refused beyond
    /// <paramref name="maxBytes"/> (compressed or uncompressed) and when the archive holds anything but one file.
    /// </summary>
    Task<ForgeArtifactDownload> DownloadArtifactAsync(
        Repository repository,
        long runId,
        string artifactName,
        long maxBytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The git blob id of <paramref name="path"/> at <paramref name="gitRef"/> (a branch, tag or sha), or null
    /// when there is no file there (D12's workflow-identity check).
    /// </summary>
    Task<string?> GetFileBlobShaAsync(Repository repository, string gitRef, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the forge to merge pull request <paramref name="number"/> if, and only if, its head is still
    /// <paramref name="sha"/> (D13). Never bypasses the repository's rules.
    /// </summary>
    /// <remarks>
    /// ⚠️ <see cref="EForgeMergeState.Pending"/> is <b>not</b> a merge (D17): the rules are evaluated after
    /// the request is accepted. Poll <see cref="GetMergeRequestStatusAsync"/> or wait for the closed event.
    /// <see cref="EForgeMergeState.HeadModified"/> right after our own push can mean the forge has not caught
    /// up yet; D17 retries it once after a short delay.
    /// </remarks>
    Task<ForgeMergeRequest> RequestMergeAsync(
        Repository repository,
        int number,
        string sha,
        EForgeMergeMethod method,
        string? commitTitle,
        string? commitMessage,
        CancellationToken cancellationToken = default);

    /// <summary>The outcome of an earlier <see cref="RequestMergeAsync"/>, by its <see cref="ForgeMergeRequest.RequestId"/>.</summary>
    Task<ForgeMergeRequest> GetMergeRequestStatusAsync(Repository repository, int number, string requestId, CancellationToken cancellationToken = default);

    /// <summary>Every review on the pull request, oldest first, with the commit each one was given on (O11's floor).</summary>
    Task<IReadOnlyList<ForgeReview>> GetReviewsAsync(Repository repository, int number, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates the queue's own check on <paramref name="sha"/> (named
    /// <see cref="CheckGate.QueueCheckName"/>), returning its id so a later call updates it.
    /// </summary>
    Task<long> PublishQueueCheckAsync(
        Repository repository,
        string sha,
        ForgeQueueCheck check,
        long? existingId,
        CancellationToken cancellationToken = default);

    /// <summary>Adds a label to the pull request.</summary>
    Task AddLabelAsync(Repository repository, int number, string label, CancellationToken cancellationToken = default);

    /// <summary>Removes a label; false when it was not there.</summary>
    Task<bool> RemoveLabelAsync(Repository repository, int number, string label, CancellationToken cancellationToken = default);

    /// <summary>Posts a new comment (never edits one) and returns its id.</summary>
    Task<long> PostCommentAsync(Repository repository, int number, string body, CancellationToken cancellationToken = default);

    /// <summary>
    /// A credential for the git-writer sidecar to push the pull request's <b>head</b> branch: the base
    /// repository for a same-repository pull request, the contributor's fork otherwise (O3).
    /// </summary>
    /// <remarks>
    /// A fork is refused unless the forge says its author allows edits from maintainers, and when the fork
    /// is gone. ⚠️ Whether the forge honours that permission for an App credential is unverified (S6); a
    /// refused push then surfaces from the writer as <c>push-rejected</c>, and the caller must say so.
    /// </remarks>
    Task<ForgePushAccess> GetPushAccessAsync(Repository repository, ForgePullRequest pullRequest, CancellationToken cancellationToken = default);
}

/// <summary>A repository role, in increasing order of power. Comparable: <c>role &gt;= Write</c>.</summary>
public enum EForgeRepositoryRole
{
    None = 0,
    Read = 1,
    Triage = 2,
    Write = 3,
    Maintain = 4,
    Admin = 5,
}

/// <summary>The rules on one branch, merged across every ruleset and classic protection.</summary>
/// <param name="RulesetsAvailable">
/// False when the forge does not offer rulesets for this repository at all (a private repository on a
/// free plan, D15). That is "no rules", not a permission failure.
/// </param>
/// <param name="RequiredChecks">Check names any rule requires. Never excludable (O12).</param>
/// <param name="RequiredApprovals">The highest approval count any rule requires.</param>
/// <param name="DismissStaleReviewsOnPush">Any rule dismisses approvals when the diff changes (O18 warns on it).</param>
/// <param name="RequireLastPushApproval">Any rule requires approval of the last push by someone else (O18 warns on it).</param>
/// <param name="RequireUpToDateBranch">Strict required checks: the head must contain the base.</param>
/// <param name="HasNativeMergeQueue">The forge's own merge queue is required on this branch (D8 refuses it).</param>
/// <param name="Rulesets">Each ruleset that applies, with whether this App may bypass it (D16).</param>
/// <param name="ClassicProtection">Whether classic branch protection is on and how much of it could be read.</param>
public sealed record ForgeBranchRules(
    bool RulesetsAvailable,
    IReadOnlyList<string> RequiredChecks,
    int RequiredApprovals,
    bool DismissStaleReviewsOnPush,
    bool RequireLastPushApproval,
    bool RequireUpToDateBranch,
    bool HasNativeMergeQueue,
    IReadOnlyList<ForgeRuleset> Rulesets,
    EForgeClassicProtection ClassicProtection);

/// <summary>One ruleset that applies to the branch.</summary>
/// <param name="RuleTypes">The forge's rule type names on this branch from this ruleset (<c>update</c>, <c>pull_request</c>, …).</param>
/// <param name="Bypass">Whether this App may bypass it (T7: ruleset 2 must be <see cref="EForgeRulesetBypass.Never"/>).</param>
public sealed record ForgeRuleset(long Id, string? Name, IReadOnlyList<string> RuleTypes, EForgeRulesetBypass Bypass);

/// <summary>GitHub's <c>current_user_can_bypass</c>, for the calling App.</summary>
public enum EForgeRulesetBypass
{
    /// <summary>The ruleset could not be read, or the forge answered a value this code does not know.</summary>
    Unknown,
    Never,
    Always,
    PullRequestsOnly,
    Exempt,
}

/// <summary>Classic branch protection, as far as the App can see it.</summary>
public enum EForgeClassicProtection
{
    /// <summary>Not protected.</summary>
    None,

    /// <summary>Protected, and its required checks and reviews were read into the rules.</summary>
    Read,

    /// <summary>
    /// Protected, but only the required checks could be read: the review settings need the
    /// <c>administration</c> permission, which the App does not ask for.
    /// </summary>
    ChecksOnly,
}

/// <summary>Everything that reported on one commit.</summary>
public sealed record ForgeCheckState(
    string Sha,
    IReadOnlyList<ForgeCheckRun> CheckRuns,
    IReadOnlyList<ForgeCommitStatus> Statuses,
    IReadOnlyList<ForgeCheckSuite> Suites);

/// <summary>A check run. <paramref name="Conclusion"/> is null until <paramref name="Status"/> is <c>completed</c>.</summary>
/// <param name="Status">The forge's word: <c>queued</c>, <c>in_progress</c>, <c>completed</c>, <c>waiting</c>, <c>requested</c>, <c>pending</c>.</param>
/// <param name="Conclusion">The forge's word: <c>success</c>, <c>failure</c>, <c>neutral</c>, <c>cancelled</c>, <c>skipped</c>, <c>timed_out</c>, <c>action_required</c>, <c>stale</c>.</param>
public sealed record ForgeCheckRun(long Id, string Name, string Status, string? Conclusion, long? SuiteId, string? AppSlug, string? DetailsUrl);

/// <summary>A commit status (the older API). <paramref name="State"/>: <c>pending</c>, <c>success</c>, <c>failure</c>, <c>error</c>.</summary>
public sealed record ForgeCommitStatus(string Context, string State, string? TargetUrl);

/// <summary>A check suite: one App's set of runs on the commit.</summary>
/// <param name="LatestCheckRunsCount">
/// ⚠️ GitHub creates a suite for every App with checks permission on every push, and one with no runs can
/// stay <c>queued</c> forever. See <see cref="CheckGate"/> for how such a suite is read.
/// </param>
public sealed record ForgeCheckSuite(long Id, string Status, string? Conclusion, string? AppSlug, int LatestCheckRunsCount);

/// <summary>What a workflow dispatch did.</summary>
/// <param name="RunId">The started run, when the forge says (D14).</param>
/// <param name="Message">The forge's own reason when <paramref name="Outcome"/> is not <see cref="EForgeDispatchOutcome.Dispatched"/>.</param>
public sealed record ForgeWorkflowDispatch(EForgeDispatchOutcome Outcome, long? RunId, string? HtmlUrl, string? Message);

public enum EForgeDispatchOutcome
{
    /// <summary>The run was created.</summary>
    Dispatched,

    /// <summary>The workflow is not registered on the default branch (S2: 404).</summary>
    WorkflowNotFound,

    /// <summary>The file at the dispatched ref has no dispatch trigger, or an input was refused (S2: 422).</summary>
    Refused,
}

/// <summary>One workflow run.</summary>
/// <param name="Status">The forge's word: <c>queued</c>, <c>in_progress</c>, <c>completed</c>, …</param>
/// <param name="Conclusion">Null until completed.</param>
/// <param name="HeadSha">The commit the run built, resolved at dispatch time (D14).</param>
public sealed record ForgeWorkflowRun(long Id, string Status, string? Conclusion, string HeadSha, string? HeadBranch, string? HtmlUrl)
{
    public bool IsCompleted => Status == "completed";
}

/// <summary>An artifact's one file, or why there is none.</summary>
public sealed record ForgeArtifactDownload(EForgeArtifactOutcome Outcome, string? FileName, byte[]? Content, string? Detail);

public enum EForgeArtifactOutcome
{
    Ok,

    /// <summary>The run has no artifact of that name.</summary>
    NotFound,

    /// <summary>The artifact exists but its retention ran out.</summary>
    Expired,

    /// <summary>The archive or its file exceeds the cap.</summary>
    TooLarge,

    /// <summary>Not a zip, or not exactly one file in it.</summary>
    Invalid,
}

public enum EForgeMergeMethod
{
    Squash,
    Merge,
    Rebase,
}

/// <summary>A merge request's state (D13, D17).</summary>
/// <param name="RequestId">The forge's id for the request (GitHub's <c>uuid</c>); null once merged or when refused synchronously.</param>
/// <param name="MergeCommitSha">The merge commit, when <see cref="EForgeMergeState.Merged"/>.</param>
/// <param name="Message">The forge's own words: why it failed, or what it is waiting for. Shown on the queue check run.</param>
public sealed record ForgeMergeRequest(EForgeMergeState State, string? RequestId, string? MergeCommitSha, string? Message);

public enum EForgeMergeState
{
    /// <summary>Accepted, not merged yet. Not a merge (D17).</summary>
    Pending,

    /// <summary>Merged; <see cref="ForgeMergeRequest.MergeCommitSha"/> says into what.</summary>
    Merged,

    /// <summary>Refused, e.g. a rule violation. A new request gets a new id.</summary>
    Failed,

    /// <summary>The head is no longer the sha we asked for (S5: GitHub's 400 "head branch was modified").</summary>
    HeadModified,
}

/// <summary>One pull-request review.</summary>
/// <param name="CommitSha">The head the review was given on; an approval counts for that commit only (O11).</param>
public sealed record ForgeReview(long Id, string? ReviewerLogin, EForgeReviewState State, string? CommitSha, DateTimeOffset? SubmittedAt);

public enum EForgeReviewState
{
    Other,
    Approved,
    ChangesRequested,
    Commented,
    Dismissed,
    Pending,
}

/// <summary>What the queue says on its own check run.</summary>
/// <param name="Summary">Markdown. Truncated to the forge's limit.</param>
/// <param name="DetailsUrl">Where "Details" leads, e.g. the repository's queue page. Must be absolute https.</param>
public sealed record ForgeQueueCheck(EForgeQueueCheckState State, string Title, string Summary, string? DetailsUrl);

public enum EForgeQueueCheckState
{
    Queued,
    InProgress,
    Success,
    Failure,
    Neutral,
    Cancelled,
}

/// <summary>A push credential for the writer sidecar, or why there is none.</summary>
/// <param name="Access">The clone/push URL of the head repository and a minted credential.</param>
/// <param name="RefusalReason">User-facing, when <paramref name="Access"/> is null.</param>
public sealed record ForgePushAccess(ForgeGitAccess? Access, string? RefusalReason)
{
    public static ForgePushAccess Refused(string reason) => new(null, reason);
}

/// <summary>
/// The forge answered something other than success, refusal-for-permission or absence: a 5xx, a rate
/// limit, an unexpected status. Retryable by the saga.
/// </summary>
public sealed class ForgeRequestException(string message, int? statusCode) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
