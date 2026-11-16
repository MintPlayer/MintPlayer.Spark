using CodeCoverage.Git.Backports;

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
    /// <summary>The workflow run's page for every tag's annotation (#544 D5); null writes none.</summary>
    public string? Run { get; set; }
    /// <summary>
    /// #552 D3: the release of a plan. The tags are written only while the tag history, without them, still has this
    /// fingerprint; otherwise the job ends <c>stale-plan</c> and writes nothing. Every tag must then be on one commit.
    /// </summary>
    public string? Fingerprint { get; set; }
    /// <summary>#568 D17: the fingerprint covers only the written packages at their majors (a release line's plan).</summary>
    public bool FingerprintScoped { get; set; }
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
    /// <summary>The workflow run's page, written into every tag (#544 D5); the app takes it from the caller's OIDC token.</summary>
    public string? Run { get; set; }
    /// <summary>#552 D1: decide the tags and write none; the response's <see cref="VersioningRunResponse.Plan"/> holds them.</summary>
    public bool PlanOnly { get; set; }
}

/// <summary>
/// <c>POST /jobs/versioning/backfill</c> (#638 D5-D8): tag every version the history of the default branch and the release
/// lines declares at its earliest occurrence, on the line of its major. A tag that already exists is left alone.
/// </summary>
public sealed class VersioningBackfillRequestWire
{
    public string? Provider { get; set; }
    public string? RepoId { get; set; }
    public string? CloneUrl { get; set; }
    public CredentialWire? Credential { get; set; }
    public string? DefaultBranch { get; set; }
    /// <summary>The tagger; the date is the writer's clock.</summary>
    public IdentityWire? Tagger { get; set; }
    public VersioningSettingsWire? Settings { get; set; }
    /// <summary>Decide the tags and write none (the onboarding's preview).</summary>
    public bool PlanOnly { get; set; }
    /// <summary>
    /// #638 D9: the collisions the owner chose to move, by tag name. Each is force-pushed onto its earliest occurrence, leased
    /// on the tag object the job fetched; a collision not named here is left.
    /// </summary>
    public List<string>? Move { get; set; }
}

/// <param name="Outcome">One of <c>created</c>, <c>already-present</c>, <c>conflict</c>, <c>unreachable</c>, <c>planned</c>.</param>
public sealed record BackfillTagWire(string Name, string Commit, string Branch, string Outcome, string? Detail);

/// <summary>An App-made tag on <paramref name="Current"/> whose version the history declares first at <paramref name="Commit"/> (D9).</summary>
/// <param name="Outcome"><c>planned</c>, <c>left</c>, <c>moved</c>, or <c>conflict</c> when the tag changed since the job fetched it.</param>
public sealed record BackfillCollisionWire(string Name, string Current, string Commit, string Branch, string Outcome, string? Detail);

/// <param name="Outcome"><see cref="JobOutcomes.Planned"/> for a plan, <see cref="JobOutcomes.Tagged"/> once written, or a failure.</param>
/// <param name="Branches">The branches walked: the default branch and the release lines.</param>
public sealed record VersioningBackfillResponse(string Outcome, string? Reason, IReadOnlyList<BackfillTagWire> Tags, IReadOnlyList<string> Branches,
    IReadOnlyList<string> Notes, string? Detail, long DurationMs)
{
    public IReadOnlyList<BackfillCollisionWire> Collisions { get; init; } = [];
}

/// <summary>The <c>versioning:</c> section of <c>coverage.yml</c> as the engine reads it (D13, D17).</summary>
public sealed class VersioningSettingsWire
{
    public List<string>? Ignore { get; set; }
    public List<LockstepGroupWire>? Groups { get; set; }
    public Dictionary<string, string>? Anchors { get; set; }
    /// <summary><c>versioning.releaseBranches</c> (D10), which names a branch a major move cuts (D12).</summary>
    public List<string>? ReleaseBranches { get; set; }
    /// <summary><c>versioning.prerelease</c> (D8); text other than a label, <c>stable</c> or <c>upstream</c> fails the request's deserialization.</summary>
    public Versioning.Versions.PrereleasePin Prerelease { get; set; }
    /// <summary><c>versioning.packages.&lt;id&gt;.prerelease</c>: a label, <c>stable</c> or <c>upstream</c>.</summary>
    public Dictionary<string, Versioning.Versions.PrereleasePin>? PrereleasePins { get; set; }
    /// <summary><c>versioning.lines</c> (#568 D15), as <see cref="Versioning.Releases.ReleaseLinePattern"/> serializes; null keeps them off.</summary>
    public ReleaseLinesWire? Lines { get; set; }
    /// <summary><c>versioning.packages.&lt;id&gt;.line</c> (#568 D19).</summary>
    public Dictionary<string, string>? LinePins { get; set; }
    /// <summary><c>versioning.lines.prerelease</c> (#568 D14): a label, <c>stable</c> or <c>upstream</c> per line.</summary>
    public Dictionary<string, Versioning.Versions.PrereleasePin>? LinePrerelease { get; set; }
}

public sealed class ReleaseLinesWire
{
    public string? Text { get; set; }
}

public sealed class LockstepGroupWire
{
    public string? Name { get; set; }
    public List<string>? Packages { get; set; }
}

/// <param name="TagOutcome">What became of its tag; null for a version a person tagged by hand, or when nothing was written.</param>
/// <param name="Release">The GitHub Release the app creates for the tag (D15); null when the tag is not in place.</param>
public sealed record ReleaseWire(string Id, string Kind, string Version, string Tag, IReadOnlyList<string> Reasons, string? ApiHash, bool TaggedByHand, string? TagOutcome,
    ReleaseNotesWire? Release = null);

public sealed record ReleaseNotesWire(string Name, string Body, bool Prerelease);

/// <summary>A release branch a platform major move asks for (D12); the app creates it behind its auto-cut policy.</summary>
public sealed record BranchCutWire(string Branch, int Major, string Commit, IReadOnlyList<string> Packages, int? PullRequest, string? Ecosystem = null);

public sealed record PackageNoteWire(string Id, string Reason);

/// <param name="Outcome"><see cref="JobOutcomes.Versioned"/> (see the per-tag outcomes), <see cref="JobOutcomes.PlanRefused"/>
/// when the plan refused a package and nothing was written, or a failure.</param>
public sealed record VersioningRunResponse(
    string Outcome, string? Reason, IReadOnlyList<ReleaseWire> Releases, IReadOnlyList<PackageNoteWire> Skipped, IReadOnlyList<PackageNoteWire> Refused,
    IReadOnlyList<string> Warnings, string? Detail, long DurationMs, IReadOnlyList<BranchCutWire>? BranchCuts = null)
{
    /// <summary>The tags a plan-only run decided (#552 D1), for <c>/jobs/tags</c> with its fingerprint to write later.</summary>
    public Versioning.Releases.TagPlan? Plan { get; init; }

    /// <summary>On a release line, the major of each package anchored on its platform (#638 D2), which the action
    /// exports to the pack. Null off a line.</summary>
    public IReadOnlyDictionary<string, int>? LineMajors { get; init; }
}

/// <summary>
/// <c>POST /jobs/versioning/plan</c> (#153 M11, PRD D3 trigger B): would a run on <see cref="Commit"/> release anything?
/// The same repository a run fetches, planned without a build; nothing is written.
/// </summary>
public sealed class VersioningPlanRequestWire
{
    public string? Provider { get; set; }
    public string? RepoId { get; set; }
    public string? CloneUrl { get; set; }
    public CredentialWire? Credential { get; set; }
    public string? Branch { get; set; }
    public string? DefaultBranch { get; set; }
    public string? Commit { get; set; }
    public int? BranchMajor { get; set; }
    public VersioningSettingsWire? Settings { get; set; }
}

/// <param name="Outcome"><see cref="JobOutcomes.Planned"/>, or a failure.</param>
/// <param name="Releases">The ids of the packages a run would release.</param>
public sealed record VersioningPlanResponse(
    string Outcome, string? Reason, IReadOnlyList<string> Releases, IReadOnlyList<PackageNoteWire> Refused, IReadOnlyList<string> Warnings, string? Detail,
    long DurationMs);

/// <summary>
/// <c>POST /jobs/versioning/ecosystems</c> (#570 M8b, PRD D5): which ecosystems a pull request touches, between the
/// merge base of <see cref="BaseBranch"/> and <see cref="HeadSha"/>, fetched through <see cref="HeadRef"/>.
/// </summary>
public sealed class VersioningEcosystemsRequestWire
{
    public string? Provider { get; set; }
    public string? RepoId { get; set; }
    public string? CloneUrl { get; set; }
    public CredentialWire? Credential { get; set; }
    public string? BaseBranch { get; set; }
    public string? HeadRef { get; set; }
    public string? HeadSha { get; set; }
    public VersioningSettingsWire? Settings { get; set; }
}

/// <param name="Outcome"><see cref="JobOutcomes.Planned"/>; <see cref="JobOutcomes.HeadMismatch"/> when the head moved; or a failure.</param>
/// <param name="Ecosystems">The ecosystems touched, sorted; empty unless planned.</param>
public sealed record VersioningEcosystemsResponse(string Outcome, string? Reason, IReadOnlyList<string> Ecosystems, string? Detail, long DurationMs);

/// <summary>
/// <c>POST /jobs/versioning/backport</c> (#153 M10, PRD D10): apply a merged pull request's change to release branches.
/// Each target is merged in memory on its own, and one that applies gets <see cref="BackportTargetWire.Head"/>, a new
/// branch for the app to open a pull request from, whatever the other targets do (#570 D6). The commit message is
/// built here from <see cref="Title"/> and the commits' ids, so a request can never carry an arbitrary message.
/// </summary>
public sealed class VersioningBackportRequestWire
{
    public string? Provider { get; set; }
    public string? RepoId { get; set; }
    public string? CloneUrl { get; set; }
    public CredentialWire? Credential { get; set; }
    /// <summary>The pull request's base branch, which must hold <see cref="Commit"/>.</summary>
    public string? SourceBranch { get; set; }
    /// <summary>The merge, squash or last rebased commit the pull request left on <see cref="SourceBranch"/>.</summary>
    public string? Commit { get; set; }
    public int PullRequest { get; set; }
    public string? Title { get; set; }
    /// <summary>The pull request's commit subjects in order: a rebase merge is told by its last N first-parent commits
    /// carrying exactly these.</summary>
    public List<string>? Subjects { get; set; }
    /// <summary>The committer; the author is the original commit's, with its date.</summary>
    public IdentityWire? Committer { get; set; }
    public List<BackportTargetWire>? Targets { get; set; }
    /// <summary>The repository's <c>backport.markers</c> (#570 D8); absent: the built-in catalog.</summary>
    public BackportMarkersWire? Markers { get; set; }
}

/// <summary>
/// <c>POST /jobs/versioning/backport/trial</c> (segment G): the backport a merge into the default branch would start, tried
/// before the merge, with nothing pushed. The change is <see cref="Base"/>..<see cref="Commit"/>, the commit the merge queue
/// is about to merge, which is not on the default branch yet and is fetched through <see cref="HeadRef"/>.
/// </summary>
public sealed class VersioningBackportTrialRequestWire
{
    public string? Provider { get; set; }
    public string? RepoId { get; set; }
    public string? CloneUrl { get; set; }
    public CredentialWire? Credential { get; set; }
    /// <summary><c>refs/pull/N/head</c>, which must be at <see cref="Commit"/>.</summary>
    public string? HeadRef { get; set; }
    /// <summary>The default branch's commit the change was tested on; an ancestor of <see cref="Commit"/>.</summary>
    public string? Base { get; set; }
    public string? Commit { get; set; }
    public List<BackportTrialTargetWire>? Targets { get; set; }
    public BackportMarkersWire? Markers { get; set; }
}

/// <summary>A line to try: the backport's own <see cref="BackportTargetWire"/> without the branch it would push to.</summary>
public sealed class BackportTrialTargetWire
{
    public string? Branch { get; set; }
    public List<AutoResolveWire>? AutoResolve { get; set; }
    public List<string>? RegeneratedPaths { get; set; }
}

/// <param name="Outcome"><c>clean</c> (it applies, through the line's rules if need be), <c>empty</c> (the line already holds
/// it) or <c>conflicted</c>.</param>
public sealed record BackportTrialTargetResult(string Branch, string Outcome, IReadOnlyList<string> Conflicts, string? Detail = null);

/// <param name="Outcome"><see cref="JobOutcomes.Tried"/>; <see cref="JobOutcomes.HeadMismatch"/> when the head moved; or a failure.</param>
public sealed record BackportTrialResponse(string Outcome, string? Reason, IReadOnlyList<BackportTrialTargetResult> Targets, string? Detail, long DurationMs);

public sealed class BackportMarkersWire
{
    /// <summary>Built-in marker kinds to leave to the plain merge (<see cref="PlatformMarkerOptions.KindNames"/>).</summary>
    public List<string>? Disable { get; set; }
    /// <summary>The repository's own platform-anchored packages, <c>*</c> as wildcard.</summary>
    public List<string>? Packages { get; set; }
}

public sealed class BackportTargetWire
{
    /// <summary>The release branch.</summary>
    public string? Branch { get; set; }
    /// <summary>The branch to push the backport to: <c>backport/&lt;pr&gt;-&lt;line&gt;</c>.</summary>
    public string? Head { get; set; }
    /// <summary>The merge queue's auto-resolve rules for this line's conflicts (the default branch's <c>coverage.yml</c>,
    /// #570 M5, PRD §5.3 step 2), as the merge queue sends them; stage 2 is the line. None: every conflict is reported.</summary>
    public List<AutoResolveWire>? AutoResolve { get; set; }
    /// <summary><c>backport.onConflict: draft</c> (#570 D7): a conflict is pushed with git's conflict markers committed,
    /// for a draft pull request, instead of only being reported.</summary>
    public bool Draft { get; set; }
    /// <summary>The merge queue's <c>regeneratedPaths</c> (#570 D9, M6): a path the change touches that matches one keeps the
    /// line's side, and is listed in <see cref="BackportTargetResult.Regenerate"/> for the line's workflow.</summary>
    public List<string>? RegeneratedPaths { get; set; }
}

/// <summary>
/// <c>POST /jobs/versioning/backport/regenerated</c> (#570 M6, D9): the line's regenerate workflow ran on a backport
/// branch; its patch is folded into the backport commit, which stays one commit on the line with its message and author.
/// Validated like the merge queue's publish (T4): only <see cref="RegeneratedPaths"/>, regular files.
/// </summary>
public sealed class VersioningBackportRegeneratedRequestWire
{
    public string? Provider { get; set; }
    public string? RepoId { get; set; }
    public string? CloneUrl { get; set; }
    public CredentialWire? Credential { get; set; }
    /// <summary>The backport branch, <c>backport/…</c>.</summary>
    public string? Head { get; set; }
    /// <summary>The backport commit the workflow ran on; a branch elsewhere is <c>branch-moved</c>.</summary>
    public string? ExpectedSha { get; set; }
    /// <summary>The workflow's <c>git diff</c>, base64; empty leaves the commit as it is.</summary>
    public string? Patch { get; set; }
    public List<string>? RegeneratedPaths { get; set; }
    public IdentityWire? Committer { get; set; }
}

/// <param name="Outcome"><c>regenerated</c> (<see cref="Commit"/> is the branch's head, the same one when the patch changed
/// nothing), <c>patch-rejected</c>, <c>branch-moved</c>, <c>push-rejected</c>, or a job failure.</param>
/// <param name="Paths">The paths the patch changed, or the ones it was refused for.</param>
public sealed record BackportRegeneratedResponse(string Outcome, string? Reason, string? Commit, IReadOnlyList<string> Paths, string? Detail, long DurationMs);

/// <param name="Commit">The backport commit; null when the target conflicts or already holds the change.</param>
/// <param name="Push">One of <c>created</c>, <c>exists</c> (the head branch is there already), <c>empty</c> (the target
/// already holds the change), <c>rejected</c>, <c>conflicted</c> (nothing was pushed to this target; the others
/// stand on their own, #570 D6), or <c>drafted</c> (a draft target's conflict, pushed with its conflict markers; its
/// <see cref="Conflicts"/> say where).</param>
/// <param name="Conflicts">The paths the target conflicts on; empty when it applies.</param>
/// <param name="PlatformChanges">The platform values the change itself edits on this line's platform, left to the merge
/// instead of normalized (#570 S2, D8); empty when there are none.</param>
/// <param name="Resolutions">The conflicts the line's auto-resolve rules resolved (PRD §5.3 steps 2 and 4); a
/// <c>regenerate</c> one still needs the line's regenerate workflow (M6).</param>
/// <param name="Detail">Why a push was rejected, or why a conflict was not resolved.</param>
/// <param name="Regenerate">The paths the line's regenerate workflow must rebuild before the pull request opens (D9, M6):
/// the regenerated paths the change touches, which kept the line's side, and the <c>regenerate</c> resolutions.</param>
public sealed record BackportTargetResult(
    string Branch, string Head, string? Commit, string? Push, IReadOnlyList<string> Conflicts, IReadOnlyList<PlatformChange> PlatformChanges,
    IReadOnlyList<ResolutionWire> Resolutions, string? Detail = null, IReadOnlyList<string>? Regenerate = null)
{
    /// <summary>The first-party packages the change raised above this line's major, which normalization dropped, so the
    /// line keeps its own version (#570 D11); the app diagnoses the upstream release.</summary>
    public IReadOnlyList<DependencyRaise> Raises { get; init; } = [];

    /// <summary>The platform values the change moved off this line's major, which normalization kept at the line's
    /// value (#638 D13); the app reports them on the backport, or on its "nothing left" close.</summary>
    public IReadOnlyList<PlatformKept> Kept { get; init; } = [];
}

/// <param name="Outcome"><see cref="JobOutcomes.Backported"/> when the job ran (each target has its own result), or a failure.</param>
/// <param name="Mode"><c>merge</c>, <c>squash</c> or <c>rebase</c>: how the pull request was merged, which decides the base.</param>
/// <param name="Base">The commit the change was taken against: the first parent, or for a rebase the commit before the run.</param>
public sealed record VersioningBackportResponse(
    string Outcome, string? Reason, string? Mode, string? Base, IReadOnlyList<BackportTargetResult> Targets, string? Detail, long DurationMs);

/// <summary>
/// <c>POST /jobs/versioning/promote</c> (segment P): move release lines to a green default-branch commit, each with one
/// merge commit whose tree is <see cref="Commit"/>'s. The commit message is built here, so a request never carries one.
/// </summary>
public sealed class VersioningPromoteRequestWire
{
    public string? Provider { get; set; }
    public string? RepoId { get; set; }
    public string? CloneUrl { get; set; }
    public CredentialWire? Credential { get; set; }
    /// <summary>The default branch, which must hold <see cref="Commit"/>.</summary>
    public string? DefaultBranch { get; set; }
    public string? Commit { get; set; }
    public IdentityWire? Committer { get; set; }
    public List<PromoteLineWire>? Lines { get; set; }
    /// <summary>For the change detection of a line with <see cref="PromoteLineWire.OnlyIfChanged"/>, and the ecosystem of a version's package.</summary>
    public VersioningSettingsWire? Settings { get; set; }
    /// <summary>The versions to release (P5), each tagged on the promotion commit of the line that releases its package.</summary>
    public List<PromoteVersionWire>? Versions { get; set; }
}

public sealed class PromoteLineWire
{
    public string? Branch { get; set; }
    /// <summary>The line's ecosystem, as the engine names it (<c>dotnet</c>, <c>angular</c>).</summary>
    public string? Ecosystem { get; set; }
    /// <summary>The line's major: a version placed on it must have it (P5).</summary>
    public int Major { get; set; }
    /// <summary>Move the line only when its ecosystem changed since its last promotion (P4).</summary>
    public bool OnlyIfChanged { get; set; }
}

public sealed class PromoteVersionWire
{
    public string? PackageId { get; set; }
    public string? Version { get; set; }
    /// <summary>True for a version recorded earlier (a hand tag): when it no longer fits it is reported, not a refusal.</summary>
    public bool Recorded { get; set; }
}

/// <param name="Outcome"><c>promoted</c>, <c>contained</c>, <c>unchanged</c>, <c>line-only</c>, <c>version-refused</c>, <c>moved</c> or <c>rejected</c>.</param>
/// <param name="Commit">The promotion commit when promoted; otherwise the line's tip.</param>
/// <param name="Paths">For <c>line-only</c>: the paths the default branch does not hold (at most 50).</param>
/// <param name="Commits">For <c>line-only</c>: the line's commits that touched them, <c>sha subject</c>, newest first.</param>
/// <param name="Tags">For <c>promoted</c>: the version tags pushed with the line (P5).</param>
public sealed record PromoteLineResult(string Branch, string Outcome, string Commit, IReadOnlyList<string> Paths, IReadOnlyList<string> Commits, string? Detail = null)
{
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>A version no promoted line took (P5): its package is unknown, or no line in the request releases it.</summary>
public sealed record PromoteVersionResult(string PackageId, string Version, string Detail);

/// <param name="Outcome"><see cref="JobOutcomes.Promoted"/> when the job ran (each line has its own result), or a failure.</param>
/// <summary><c>POST /jobs/versioning/hand-tag</c> (segment P, P5): a person's new tag, checked and deleted.</summary>
public sealed class HandTagRequestWire
{
    public string? Provider { get; set; }
    public string? RepoId { get; set; }
    public string? CloneUrl { get; set; }
    public CredentialWire? Credential { get; set; }
    /// <summary>The tag's name, a version tag <c>&lt;PackageId&gt;/v&lt;version&gt;</c>.</summary>
    public string? Tag { get; set; }
    /// <summary>What the webhook said the tag points at; the deletion is leased on it.</summary>
    public string? TagObject { get; set; }
    /// <summary>Every release line, current and past (<see cref="PromoteLineWire.OnlyIfChanged"/> is ignored; segment T).</summary>
    public List<PromoteLineWire>? Lines { get; set; }
    /// <summary><c>versioning.lines.current: promote</c>: a tag of a current line's major is recorded for its next promotion.</summary>
    public bool Promotes { get; set; }
    public VersioningSettingsWire? Settings { get; set; }
}

/// <param name="Outcome"><see cref="JobOutcomes.Planned"/> when the job ran, or a failure.</param>
/// <param name="Tag"><c>recorded</c> (deleted, for the next promotion), <c>accepted</c> (left in place), <c>refused</c>
/// (deleted), <c>not-a-package</c> (left alone) or <c>moved</c> (left alone).</param>
/// <param name="Branch">The line the tag belongs to: the current line for <c>recorded</c>, the line of its major otherwise.</param>
/// <param name="Commit">The commit the tag pointed at, for the comment on a refused one.</param>
/// <param name="Detail">For <c>refused</c>: why; for a failure: what failed.</param>
public sealed record HandTagResponse(string Outcome, string? Reason, string? Tag, string? Branch, string? Commit, string? Detail, long DurationMs);

/// <param name="Unplaced">The versions that belong to no line of the request; nothing was refused for them.</param>
public sealed record VersioningPromoteResponse(string Outcome, string? Reason, IReadOnlyList<PromoteLineResult> Lines, string? Detail, long DurationMs)
{
    public IReadOnlyList<PromoteVersionResult> Unplaced { get; init; } = [];
}

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
    /// <summary>Backport: every target applied; each has its own push result.</summary>
    public const string Backported = "backported";
    /// <summary>Promote (segment P): the job ran; each line has its own result.</summary>
    public const string Promoted = "promoted";
    /// <summary>Backport trial (segment G): every target was tried and nothing pushed; each has its own result.</summary>
    public const string Tried = "tried";
    /// <summary>Backport regeneration (#570 M6): the workflow's patch is in the backport commit.</summary>
    public const string Regenerated = "regenerated";
    /// <summary>Backport: a target conflicts (see its paths), so no target was pushed (D10).</summary>
    public const string Conflicted = "conflicted";
    /// <summary>Versioning plan: what a run would release, nothing written (D3 trigger B).</summary>
    public const string Planned = "planned";
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
    /// <summary>#552 D3: the tag history moved since the plan was made, so its tags were not written.</summary>
    public const string StalePlan = "stale-plan";
    /// <summary>
    /// Not a failure: <c>pushed</c> or <c>published</c> with the commit an earlier attempt of the same job already pushed,
    /// whose answer never reached the saga (merge-queue PRD D51 (1), D52). The branch was left as it was.
    /// </summary>
    public const string AlreadyPushed = "already-pushed";
    /// <summary>Versioning run: the repository cannot be planned (a project graph past its caps, a packed package the
    /// graph does not hold, the built commit not on the branch); <c>detail</c> says which. Rerunning changes nothing.</summary>
    public const string Unplannable = "unplannable";
}
