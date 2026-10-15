using CodeCoverage.Entities;

namespace CodeCoverage.Forge;

/// <summary>
/// What the merge queue needs from a forge (merge-queue PRD §5.4, M4): roles, branch rules, check
/// state, the regenerate workflow, the merge itself and the queue's own feedback.
/// </summary>
/// <remarks>
/// <para>
/// <b>A narrow interface of its own</b>, beside <see cref="IForgeIntegration"/> rather than inside it, the
/// same way <see cref="IForgeInstallationPermissions"/> is: the queue is one feature, and its twenty-odd
/// members would more than double the width of the seam every other caller has to read. A forge supports it when its
/// <see cref="IForgeIntegration.Capabilities"/> lists <see cref="EForgeCapability.MergeQueue"/>, and then it
/// must also register exactly one implementation of this interface for its <see cref="Provider"/>; the
/// conformance test holds both directions. GitHub only in this PR (O7); GitLab and Bitbucket follow under #422.
/// </para>
/// <para>
/// <b>No forge type appears in any signature</b>, and no credential either, except
/// <see cref="GetWriterAccessAsync"/> and <see cref="RevokeWriterAccessAsync"/>, which exist to hand one job's
/// credential to the git-writer sidecar and to end it (T9).
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
    /// latest base", and the moved-base check right before the merge).
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
    /// The names of every check run and commit status on <paramref name="sha"/>, ordinal-sorted and distinct, the
    /// queue's own check left out: O12's expected-minimum set when <paramref name="sha"/> is the head pinned at enqueue
    /// (D45; not the base branch's latest commit, whose push-only workflows never run on a pull request's commit).
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
    /// The workflow definitions at <paramref name="gitRef"/> (a branch or a sha): every file the forge would read as a
    /// workflow there, with its text (on GitHub, each <c>*.yml</c>/<c>*.yaml</c> directly in <c>.github/workflows</c>),
    /// in path order. Empty when there is none. The fork precondition reads them before the queue pushes a fork's code to
    /// a queue branch (§0 D65).
    /// </summary>
    /// <remarks>
    /// A workflow the forge cannot hand over whole (a symlink, a submodule, over 1 MiB, not UTF-8, or past
    /// <see cref="ForgeWorkflowFile.MaxFiles"/>) is listed with a null <see cref="ForgeWorkflowFile.Content"/> and the
    /// reason, never left out: the caller must count what it cannot read as unsafe.
    /// </remarks>
    Task<IReadOnlyList<ForgeWorkflowFile>> GetWorkflowFilesAsync(Repository repository, string gitRef, CancellationToken cancellationToken = default);

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
    /// The pull request's commits, oldest first, with their authors and messages: facts for the queue's
    /// <c>Co-authored-by:</c> trailers (§0 D60), whom to credit being the queue's call. The forge's own limit applies:
    /// GitHub lists at most 250 commits of a pull request.
    /// </summary>
    Task<IReadOnlyList<ForgePullRequestCommit>> GetPullRequestCommitsAsync(Repository repository, int number, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every open pull request whose base is <paramref name="baseBranch"/>, drafts included (each says whether it is one),
    /// in the forge's own order. Empty when there is none. The merge-queue panel offers them as queue candidates, so a
    /// maintainer picks a pull request instead of typing its number.
    /// </summary>
    Task<IReadOnlyList<ForgeOpenPullRequest>> ListOpenPullRequestsAsync(Repository repository, string baseBranch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates the queue's own check on <paramref name="sha"/> (named
    /// <see cref="CheckGate.QueueCheckName"/>), returning its id so a later call updates it. A check with
    /// <see cref="ForgeQueueCheck.Actions"/> needs <see cref="EForgeCapability.CheckButtons"/>: a merge queue whose forge
    /// does not declare it refuses one with <see cref="NotSupportedException"/> (merge-queue PRD §0 O30).
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
    /// What ONE git-writer job is handed (T9, merge-queue PRD §0 D59): the base repository's clone URL, where the job
    /// pushes, and a credential minted for this job alone. It reaches <paramref name="repository"/> and nothing else, with
    /// contents write, plus the forge's workflow permission only when <paramref name="pushesWorkflowChanges"/>. It is never
    /// cached and never handed to another job; <see cref="RevokeWriterAccessAsync"/> ends it when the job is over.
    /// </summary>
    /// <param name="pullRequest">
    /// The pull request whose head branch the job pushes: in the base repository, or in the contributor's fork (O3). Null
    /// for a job that pushes only to the base repository (the queue branch: prepare, its reset, its deletion).
    /// </param>
    /// <param name="pushesWorkflowChanges">
    /// The job's push may carry <c>.github/workflows/**</c> content the target repository has on no branch, which GitHub
    /// refuses an App without its <c>workflows</c> permission.
    /// </param>
    /// <remarks>
    /// A fork is refused unless the forge says its author allows edits from maintainers, and when the fork is gone. ⚠️ Even
    /// for a fork's push the credential is scoped to the BASE repository: "Allow edits by maintainers" lets those with
    /// push access to the base repository push to the fork's branch, and an App has no token for a fork it is not
    /// installed on. GitHub honours that for an App's token (S6, measured live in §0 D69), but refuses its write of a
    /// workflow file the fork has on no branch, whatever the credential carries; a refused push surfaces from the writer
    /// as <c>push-rejected</c> with the forge's reason, and the caller must say so (§0 D71).
    /// </remarks>
    Task<ForgePushAccess> GetWriterAccessAsync(Repository repository, ForgePullRequest? pullRequest, bool pushesWorkflowChanges, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends a writer job's credential once the job is over (T9, §0 D59), instead of letting it live out the forge's own
    /// lifetime (an hour on GitHub). Best effort and never throws: false when the forge refused or could not be reached,
    /// and the credential then expires by itself.
    /// </summary>
    Task<bool> RevokeWriterAccessAsync(Repository repository, ForgeWriterAccess access, CancellationToken cancellationToken = default);
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
/// <summary>One workflow definition (§0 D65): its repository path and text, or, when the forge could not hand it over
/// whole, a null <paramref name="Content"/> and <paramref name="Unreadable"/>, the reason.</summary>
public sealed record ForgeWorkflowFile(string Path, string? Content, string? Unreadable = null)
{
    /// <summary>The largest workflow file read; a larger one is listed unreadable.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The most workflow files read at one ref; the rest are listed unreadable.</summary>
    public const int MaxFiles = 200;
}

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

/// <summary>One commit of a pull request (§0 D60).</summary>
/// <param name="AuthorName">The author name git recorded.</param>
/// <param name="AuthorEmail">The author email git recorded.</param>
/// <param name="AuthorLogin">The forge account the commit is attributed to, when the forge knows one.</param>
/// <param name="AuthorIsBot">The forge says that account is a bot.</param>
/// <param name="Message">The whole commit message, trailers included.</param>
public sealed record ForgePullRequestCommit(string Sha, string? AuthorName, string? AuthorEmail, string? AuthorLogin, bool AuthorIsBot, string Message);

/// <summary>One open pull request, as <see cref="IForgeMergeQueue.ListOpenPullRequestsAsync"/> lists it.</summary>
/// <param name="Number">The pull request's number in its repository.</param>
/// <param name="Title">Its title, as the author wrote it.</param>
/// <param name="BaseRef">The branch it targets.</param>
/// <param name="AuthorLogin">The forge account that opened it, when the forge says.</param>
/// <param name="IsDraft">The forge marks it a draft; the queue does not offer drafts.</param>
public sealed record ForgeOpenPullRequest(int Number, string Title, string BaseRef, string? AuthorLogin, bool IsDraft);

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
public sealed record ForgeQueueCheck(EForgeQueueCheckState State, string Title, string Summary, string? DetailsUrl)
{
    /// <summary>
    /// Buttons the forge shows on the check once it is completed; a click comes back to the App as a command (merge-queue
    /// PRD §0 O30). Only a forge that declares <see cref="EForgeCapability.CheckButtons"/> takes them: any other merge
    /// queue refuses a check that carries one with <see cref="NotSupportedException"/>.
    /// </summary>
    public IReadOnlyList<ForgeQueueCheckAction> Actions { get; init; } = [];

    /// <summary>
    /// The pull request the check speaks for. The forge keeps it with the check, so a click on one of its
    /// <see cref="Actions"/> names the pull request even where the forge's own link is missing: GitHub lists no pull
    /// request on the check run of a fork's commit.
    /// </summary>
    public int? PullRequestNumber { get; init; }
}

/// <summary>A button on the queue's own check (merge-queue PRD §0 O30): what it says, and what a click sends back.</summary>
/// <param name="Label">The button's text. GitHub takes at most <see cref="MaxLabelLength"/> characters.</param>
/// <param name="Description">What the button does, shown with it. GitHub takes at most <see cref="MaxDescriptionLength"/>.</param>
/// <param name="Identifier">What a click sends back to the App. GitHub takes at most <see cref="MaxIdentifierLength"/>.</param>
public sealed record ForgeQueueCheckAction(string Label, string Description, string Identifier)
{
    /// <summary>GitHub's limits on a check run's requested actions.</summary>
    public const int MaxActions = 3, MaxLabelLength = 20, MaxDescriptionLength = 40, MaxIdentifierLength = 20;

    /// <summary>
    /// "Add to merge queue": the clicker's enqueue command, which the queue authorises and refuses exactly as it does a
    /// label or a <c>/queue</c> comment.
    /// </summary>
    public static readonly ForgeQueueCheckAction Enqueue = new("Add to merge queue", "Rebase, regenerate, check, then merge", "mq-enqueue");

    /// <summary>
    /// "Remove from queue", on the check of a pull request waiting in line (merge-queue PRD §0 O32): the clicker's dequeue
    /// command, which the queue authorises and refuses exactly as it does a <c>/dequeue</c> comment.
    /// </summary>
    public static readonly ForgeQueueCheckAction Dequeue = new("Remove from queue", "Take this pull request out of the queue", "mq-dequeue");
}

public enum EForgeQueueCheckState
{
    Queued,
    InProgress,
    Success,
    Failure,
    Neutral,
    Cancelled,
}

/// <summary>One git-writer job's access, or why there is none (<see cref="IForgeMergeQueue.GetWriterAccessAsync"/>).</summary>
/// <param name="Access">Where the job clones and pushes, and its credential.</param>
/// <param name="RefusalReason">User-facing, when <paramref name="Access"/> is null.</param>
public sealed record ForgePushAccess(ForgeWriterAccess? Access, string? RefusalReason)
{
    public static ForgePushAccess Refused(string reason) => new(null, reason);
}

/// <summary>What one git-writer job reaches the forge with (T9, §0 D59).</summary>
/// <param name="CloneUrl">The base repository's https clone URL. Every job clones it, and fetches the pull request's head from it.</param>
/// <param name="PushUrl">Where the pull request's head branch lives: <paramref name="CloneUrl"/>, or the fork's (O3).</param>
/// <param name="Username">The credential's user name (<c>x-access-token</c> on GitHub).</param>
/// <param name="Password">⚠️ The job's own token. Never log or store it.</param>
public sealed record ForgeWriterAccess(string CloneUrl, string PushUrl, string Username, string Password)
{
    /// <summary>Redacted: a record prints every member, and this one carries a credential.</summary>
    public override string ToString() => $"ForgeWriterAccess {{ CloneUrl = {CloneUrl}, PushUrl = {PushUrl}, Username = {Username}, Password = *** }}";
}

/// <summary>
/// The forge answered something other than success, refusal-for-permission or absence: a 5xx, a rate
/// limit, an unexpected status. Retryable by the saga.
/// </summary>
public sealed class ForgeRequestException(string message, int? statusCode) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
