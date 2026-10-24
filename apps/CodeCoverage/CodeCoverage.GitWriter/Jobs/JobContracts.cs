namespace CodeCoverage.GitWriter.Jobs;

// The writer's wire contract (camelCase JSON). Every request names the repository it works on and carries a short-lived,
// single-repository forge token minted for that one job (T9); nothing survives the job. Every answer is 200 with an
// outcome; a request that does not validate is a 400 with a RefusedResponse naming the field and its rule (§0 D57).

/// <summary>A forge credential, used for this job only and never stored.</summary>
public sealed class CredentialWire
{
    public string? Username { get; set; }
    public string? Password { get; set; }

    public override string ToString() => $"CredentialWire {{ Username = {Username} }}";
}

/// <summary>A commit identity. Every job that writes a commit requires <see cref="Date"/> (§0 D65): a date git stamped
/// itself would make the same request build another commit.</summary>
public sealed class IdentityWire
{
    public string? Name { get; set; }
    public string? Email { get; set; }
    public DateTimeOffset? Date { get; set; }
}

/// <summary>One auto-resolve entry (O1, O2, §10.3): a path glob and its strategy. For take-base, the top-level JSON members
/// whose lines may conflict, or <see cref="AnyLine"/> for a file whose every line may (open question Q7).</summary>
public sealed class AutoResolveWire
{
    public string? Path { get; set; }
    public List<string>? Keys { get; set; }
    public bool AnyLine { get; set; }

    /// <summary>One of <c>MergeQueueWireRules.Strategies</c>; null is take-base (an older app).</summary>
    public string? Strategy { get; set; }

    /// <summary>The validators the resolved content must pass (at most <c>MergeQueueWireRules.MaxValidators</c>).</summary>
    public List<string>? Validate { get; set; }

    /// <summary>Whether a resolution needs the regenerate workflow; null is the strategy's default. take-base with false is
    /// refused: its result is stale by design.</summary>
    public bool? Regenerate { get; set; }
}

/// <summary>One patch of the publish (O36): the <c>merge-queue-patch</c> of one regenerate workflow's run.</summary>
public sealed class PatchWire
{
    /// <summary>The workflow file the patch came from, for messages; the patches apply in the order given.</summary>
    public string? Workflow { get; set; }

    /// <summary>The run's <c>git diff</c>, base64; empty when the run changed nothing.</summary>
    public string? Patch { get; set; }
}

/// <summary>How one conflicted path was resolved (§10.6).</summary>
/// <param name="Rule">The index of the matching <c>autoResolve</c> entry in the request.</param>
public sealed record ResolutionWire(string Path, string Strategy, bool Regenerate, int Rule);

/// <summary>
/// <c>POST /jobs/prepare</c> (§5.1 step 1, §0 D65): squashes and rebases the pinned head onto the base and answers the
/// commit's id. It pushes nothing. The commit is a function of these fields alone (the dates are the saga's), so
/// <c>POST /jobs/queue-branch</c> and <c>POST /jobs/publish</c>, which carry the same fields, rebuild exactly it.
/// </summary>
public class PrepareRequestWire
{
    public string? Provider { get; set; }
    public string? RepoId { get; set; }
    /// <summary>The base repository's https clone URL; the pull request's head is fetched from it too
    /// (<see cref="HeadRef"/>, e.g. <c>refs/pull/12/head</c>, which also exists for a fork's pull request).</summary>
    public string? CloneUrl { get; set; }
    public CredentialWire? Credential { get; set; }
    public string? BaseBranch { get; set; }
    /// <summary>
    /// Prepare: when set, the base branch must still be at this commit, else <c>base-moved</c>. The queue-branch push and
    /// the publish: required, the commit the prepared one was built on, which the base branch may have moved past since.
    /// </summary>
    public string? BaseSha { get; set; }
    public string? HeadRef { get; set; }
    /// <summary>The head SHA pinned at enqueue (T3); a head at any other commit is <c>head-mismatch</c>. The publish's
    /// lease on the pull request's branch too.</summary>
    public string? HeadSha { get; set; }
    public List<AutoResolveWire>? AutoResolve { get; set; }
    /// <summary>With its date, like <see cref="Committer"/> (§0 D65).</summary>
    public IdentityWire? Author { get; set; }
    public IdentityWire? Committer { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// <c>POST /jobs/queue-branch</c> (§0 D65): rebuilds the prepared commit from the prepare's fields, checks that it is
/// <see cref="PreparedSha"/>, and pushes it to <see cref="QueueBranch"/> under a lease. The saga asks for it only right
/// before it dispatches the regenerate workflow: a push to a branch of the base repository starts that repository's
/// <c>push</c> workflows on the pushed code (D14, D19).
/// </summary>
public sealed class QueueBranchPushWire : PrepareRequestWire
{
    /// <summary>The commit the prepare answered; a rebuild that is not it is <c>prepared-differs</c>.</summary>
    public string? PreparedSha { get; set; }
    /// <summary><c>mq/&lt;base&gt;/pr-&lt;n&gt;</c> (D5).</summary>
    public string? QueueBranch { get; set; }
    /// <summary>The lease on the queue branch: the commit it must still be at, or null for "must not exist".</summary>
    public string? ExpectedQueueSha { get; set; }

    /// <summary>
    /// Required: whether the pull request's head lives in a fork (O3). A fork's commit whose <c>.github</c> differs from
    /// its base's is then refused (<c>fork-touches-workflows</c>) and nothing is pushed, whatever else the request says:
    /// the push would run the fork's workflow files with the base repository's secrets (O25, §0 D67). The saga never asks
    /// for that push; this holds if it did.
    /// </summary>
    public bool? HeadFromFork { get; set; }
}

/// <summary>
/// <c>POST /jobs/publish</c> (§5.1 step 3, §0 D65): rebuilds the prepared commit from the prepare's fields (never from the
/// queue branch, which exists only when a dispatch needed it), checks that it is <see cref="PreparedSha"/>, applies the
/// patch and replaces the pull request's branch under a lease on <see cref="PrepareRequestWire.HeadSha"/>.
/// </summary>
public sealed class PublishRequestWire : PrepareRequestWire
{
    /// <summary>Where the pull request's branch lives, when that is not <see cref="PrepareRequestWire.CloneUrl"/> (a fork, O3).</summary>
    public string? PushUrl { get; set; }
    /// <summary>The commit the prepare answered; a rebuild that is not it is <c>prepared-differs</c>.</summary>
    public string? PreparedSha { get; set; }
    public string? PrBranch { get; set; }
    /// <summary>The regenerate artifact's <c>git diff</c>, base64; null or empty publishes the prepared tree as is. An
    /// older app's single patch; with <see cref="Patches"/> it must be null.</summary>
    public string? Patch { get; set; }

    /// <summary>
    /// One patch per regenerate workflow run (O36), applied together: two patches that change one path differently are
    /// refused (<c>overlap</c>), identical sections are kept once. Their total is held to <c>MaxPatchBytes</c>.
    /// </summary>
    public List<PatchWire>? Patches { get; set; }
    /// <summary>The globs the patch may touch (<c>coverage.yml</c> <c>regeneratedPaths</c>, read from the default branch,
    /// O16). May be empty when there is no regenerate step (§0 D57); a patch against an empty list is refused (T4).</summary>
    public List<string>? RegeneratedPaths { get; set; }
}

/// <summary><c>DELETE /jobs/queue-branch</c>: removes <c>mq/…</c> when the entry ends (D5).</summary>
public sealed class DeleteQueueBranchWire
{
    public string? Provider { get; set; }
    public string? RepoId { get; set; }
    public string? CloneUrl { get; set; }
    public CredentialWire? Credential { get; set; }
    public string? QueueBranch { get; set; }
    /// <summary>When set, the branch is deleted only while it is at this commit (a lease).</summary>
    public string? ExpectedSha { get; set; }
}

/// <param name="Paths">Why, for <c>evicted</c>. For <c>prepared</c>: the <c>.github/**</c> paths the commit changes
/// against its base when <see cref="TouchesWorkflows"/>.</param>
public sealed record PrepareResponse(
    string Outcome, string? Reason, string? BaseSha, string? HeadSha, string? PreparedSha,
    IReadOnlyList<string> ResolvedPaths, IReadOnlyList<string> Paths, string? Detail, long DurationMs)
{
    /// <summary>The prepared commit changes <c>.github/**</c> against its base (D12, §0 D65): the pull request's own change,
    /// or one a rename on the base carries there. Never refused here, a fork's included, since the prepare pushes nothing:
    /// the saga decides what it stops (O21 A, O25), and the queue-branch push refuses a fork's itself (§0 D67).</summary>
    public bool TouchesWorkflows { get; init; }

    /// <summary>
    /// The prepared commit's <c>.github/workflows</c> differs from the pinned head's (§0 D59, D67): the base changed workflow
    /// files since. A fork's branch, which holds the head, lacks them, and GitHub refuses an App's push of a workflow file
    /// the fork has on no branch whatever the token's permissions (§0 D69), so the saga names this when such a publish is
    /// refused. The pull request's own workflow change is <see cref="TouchesWorkflows"/> instead.
    /// </summary>
    public bool BaseChangedWorkflows { get; init; }

    /// <summary>For <c>prepared</c>: how each path in <see cref="ResolvedPaths"/> was resolved (§10.6). An app that does
    /// not read it treats every resolved path as needing regeneration, as before.</summary>
    public IReadOnlyList<ResolutionWire> Resolutions { get; init; } = [];

    /// <summary>The resolver's version and the writer's <c>git --version</c> (O35 #4): when a later job rebuilds another
    /// commit and this changed, the saga restarts the entry instead of evicting it.</summary>
    public string? ResolverVersion { get; init; }
}

/// <param name="PreparedSha">For <c>pushed</c>: the commit the queue branch holds now.</param>
/// <param name="Paths">Why, for <c>evicted</c> and <c>fork-touches-workflows</c>.</param>
public sealed record QueueBranchPushResponse(string Outcome, string? Reason, string? PreparedSha, IReadOnlyList<string> Paths, string? Detail, long DurationMs)
{
    /// <summary>The writer's resolver version (<see cref="PrepareResponse.ResolverVersion"/>), with every answer.</summary>
    public string? ResolverVersion { get; init; }
}

public sealed record PublishResponse(string Outcome, string? Reason, string? FinalSha, IReadOnlyList<string> Paths, string? Detail, long DurationMs)
{
    /// <summary>The writer's resolver version (<see cref="PrepareResponse.ResolverVersion"/>), with every answer.</summary>
    public string? ResolverVersion { get; init; }
}

public sealed record DeleteQueueBranchResponse(string Outcome, string? Reason, string? Detail, long DurationMs);

/// <summary>
/// <c>POST /jobs/tags</c> (#153 M5): the server host's version tags. The writer builds each annotation itself from
/// <see cref="TagWire.ApiHash"/>, so a request can never carry an arbitrary tag message.
/// </summary>
public sealed class TagsRequestWire
{
    public string? Provider { get; set; }
    public string? RepoId { get; set; }
    public string? CloneUrl { get; set; }
    public CredentialWire? Credential { get; set; }
    /// <summary>The branches a tagged commit must be on: the default branch and the release branches (D10).</summary>
    public List<string>? Branches { get; set; }
    /// <summary>The tagger; the date is the writer's clock.</summary>
    public IdentityWire? Tagger { get; set; }
    public List<TagWire>? Tags { get; set; }
}

public sealed class TagWire
{
    /// <summary><c>&lt;PackageId&gt;/v&lt;version&gt;</c> (D1).</summary>
    public string? Name { get; set; }
    public string? Commit { get; set; }
    /// <summary><c>sha256:&lt;64 hex&gt;</c>, or null when the package's API could not be hashed (D6).</summary>
    public string? ApiHash { get; set; }
}

/// <param name="Outcome">One of <c>created</c>, <c>already-present</c>, <c>conflict</c>, <c>unreachable</c>.</param>
public sealed record TagResultWire(string Name, string Outcome, string? Detail);

/// <param name="Outcome"><see cref="JobOutcomes.Tagged"/> with one result per tag, or a failure.</param>
public sealed record TagsResponse(string Outcome, string? Reason, IReadOnlyList<TagResultWire> Tags, string? Detail, long DurationMs);

/// <summary>
/// <c>POST /jobs/versioning/run</c> (#153 M6): one whole versioning run on the server host. The writer fetches
/// <see cref="Branch"/> (and <see cref="DefaultBranch"/>) with the tags, plans from <see cref="Manifest"/> with the
/// engine and pushes the tags. The main app has decided everything a token's claims decide; this decides nothing the
/// engine would not decide the same way in the action's local host.
/// </summary>
public sealed class VersioningRunRequestWire
{
    public string? Provider { get; set; }
    public string? RepoId { get; set; }
    public string? CloneUrl { get; set; }
    public CredentialWire? Credential { get; set; }
    /// <summary>The branch the workflow ran on: the default branch or a release branch (D10).</summary>
    public string? Branch { get; set; }
    public string? DefaultBranch { get; set; }
    /// <summary>The release line's major when <see cref="Branch"/> is a release branch whose name carries one.</summary>
    public int? BranchMajor { get; set; }
    public IdentityWire? Tagger { get; set; }
    public VersioningSettingsWire? Settings { get; set; }
    public Versioning.Releases.ReleaseManifest? Manifest { get; set; }
}

/// <summary>The <c>versioning:</c> section of <c>coverage.yml</c> as the engine reads it (D13, D17).</summary>
public sealed class VersioningSettingsWire
{
    public List<string>? Ignore { get; set; }
    public List<LockstepGroupWire>? Groups { get; set; }
    public Dictionary<string, string>? Anchors { get; set; }
}

public sealed class LockstepGroupWire
{
    public string? Name { get; set; }
    public List<string>? Packages { get; set; }
}

/// <param name="TagOutcome">What became of its tag; null for a version a person tagged by hand, or when nothing was written.</param>
public sealed record ReleaseWire(string Id, string Kind, string Version, string Tag, IReadOnlyList<string> Reasons, string? ApiHash, bool TaggedByHand, string? TagOutcome);

public sealed record PackageNoteWire(string Id, string Reason);

/// <param name="Outcome"><see cref="JobOutcomes.Versioned"/> (see the per-tag outcomes), <see cref="JobOutcomes.PlanRefused"/>
/// when the plan refused a package and nothing was written, or a failure.</param>
public sealed record VersioningRunResponse(
    string Outcome, string? Reason, IReadOnlyList<ReleaseWire> Releases, IReadOnlyList<PackageNoteWire> Skipped, IReadOnlyList<PackageNoteWire> Refused,
    IReadOnlyList<string> Warnings, string? Detail, long DurationMs);

/// <summary>The body of a 400 (§0 D57): the first field that broke its rule, and the rule. Never the value.</summary>
public sealed record RefusedResponse(string Outcome, string Field, string Detail);

/// <summary>The <c>outcome</c> values.</summary>
public static class JobOutcomes
{
    /// <summary>Prepare: the squashed commit is built; its id is <c>preparedSha</c>. Nothing was pushed (§0 D65).</summary>
    public const string Prepared = "prepared";
    /// <summary>The content may not be merged by the bot; <c>reason</c> and <c>paths</c> say why.</summary>
    public const string Evicted = "evicted";
    /// <summary>Prepare and the queue-branch push: the pull request's head is not the pinned SHA (T3).</summary>
    public const string HeadMismatch = "head-mismatch";
    /// <summary>Prepare: the base branch is no longer at <c>baseSha</c>. The queue-branch push and the publish:
    /// <c>baseSha</c> is no longer a commit the forge has.</summary>
    public const string BaseMoved = "base-moved";
    /// <summary>The queue-branch push: the queue branch holds the prepared commit (<c>preparedSha</c>).</summary>
    public const string Pushed = "pushed";
    /// <summary>
    /// The queue-branch push: the request's head is a fork's and the prepared commit's <c>.github</c> differs from its
    /// base's (<c>paths</c>), so nothing was pushed, whatever else the request said (O25, §0 D67). The saga ends the entry.
    /// </summary>
    public const string ForkTouchesWorkflows = "fork-touches-workflows";
    /// <summary>
    /// The queue-branch push and the publish: the prepared commit rebuilt from the request is not <c>preparedSha</c>
    /// (§0 D65). Sending the same request again builds the same commit, so the saga does not retry it.
    /// </summary>
    public const string PreparedDiffers = "prepared-differs";
    /// <summary>The queue-branch push: the queue branch is not where the lease expected it.</summary>
    public const string QueueBranchMoved = "queue-branch-moved";
    /// <summary>Publish: the regenerate patch failed validation (T4); <c>reason</c> names the rule.</summary>
    public const string PatchRejected = "patch-rejected";
    /// <summary>Publish: the pull request's branch is not at <c>headSha</c> (the author pushed), seen before the push or
    /// as the lease's rejection (§5.1 "branch moved during queue run").</summary>
    public const string BranchMoved = "branch-moved";
    /// <summary>Publish: the branch now holds the final commit.</summary>
    public const string Published = "published";
    /// <summary>The forge refused the push for another reason (a rule, a permission); <c>detail</c> has its message.</summary>
    public const string PushRejected = "push-rejected";
    /// <summary>Delete: the branch is gone (or was never there).</summary>
    public const string Deleted = "deleted";
    /// <summary>Tags: every tag has its own result; a conflict or an unreachable commit is one of them, not a failure.</summary>
    public const string Tagged = "tagged";
    /// <summary>Versioning run: planned, and every tag has its own result.</summary>
    public const string Versioned = "versioned";
    /// <summary>Versioning run: the plan refused a package (D10, D13), so no tag was written.</summary>
    public const string PlanRefused = "plan-refused";
    /// <summary>Every slot was taken for <c>SlotWaitSeconds</c>; retry later.</summary>
    public const string Busy = "busy";
    /// <summary>An operational failure; <c>reason</c> is one of <see cref="JobReasons"/>.</summary>
    public const string Failed = "failed";
    /// <summary>The 400's outcome: the request broke a rule, named in <see cref="RefusedResponse"/>. Sending it again
    /// changes nothing, so the saga never retries it (§0 D57).</summary>
    public const string Refused = "refused";
}

/// <summary>The <c>reason</c> of a failed job.</summary>
public static class JobReasons
{
    public const string Timeout = "timeout";
    public const string TooLarge = "too-large";
    public const string DiskFloor = "disk-floor";
    public const string Auth = "auth";
    public const string NotFound = "not-found";
    public const string Error = "error";
    /// <summary>
    /// Not a failure: <c>pushed</c> or <c>published</c> with the commit an earlier attempt of the same job already pushed,
    /// whose answer never reached the saga (merge-queue PRD D51 (1), D52). The branch was left as it was.
    /// </summary>
    public const string AlreadyPushed = "already-pushed";
    /// <summary>Versioning run: the repository cannot be planned (a project graph past its caps, a packed package the
    /// graph does not hold, the built commit not on the branch); <c>detail</c> says which. Rerunning changes nothing.</summary>
    public const string Unplannable = "unplannable";
}
