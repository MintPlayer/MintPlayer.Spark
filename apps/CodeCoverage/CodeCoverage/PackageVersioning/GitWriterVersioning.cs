using CodeCoverage.CommitGraph;
using CodeCoverage.Git.Backports;
using CodeCoverage.MergeQueue;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// The body of the git writer's <c>POST /jobs/versioning/run</c> (#153 M6). The settings and the manifest are the engine's
/// own types, so what the writer reads is exactly what the app validated.
/// </summary>
public sealed record GitWriterVersioningRequest(
    string Provider, string RepoId, string CloneUrl, SidecarCredential Credential, string Branch, string DefaultBranch, int? BranchMajor,
    GitWriterTagger Tagger, VersioningSettings Settings, ReleaseManifest Manifest, string? Run = null, bool PlanOnly = false);

public sealed record GitWriterTagger(string Name, string Email);

/// <summary>
/// The body of the git writer's <c>POST /jobs/tags</c>: tags pushed outside a run, which is how the onboarding's seed tags
/// (D17) reach the repository. A seed carries no <see cref="GitWriterTag.ApiHash"/>, so its annotation says nothing an API
/// comparison could trust, and no GitHub Release is made for it: releases come only from a run's result.
/// </summary>
public sealed record GitWriterTagsRequest(
    string Provider, string RepoId, string CloneUrl, SidecarCredential Credential, IReadOnlyList<string> Branches, GitWriterTagger Tagger,
    IReadOnlyList<GitWriterTag> Tags, string? Run = null, string? Fingerprint = null, bool FingerprintScoped = false);

public sealed record GitWriterTag(string Name, string Commit, string? ApiHash = null);

/// <param name="Outcome"><c>tagged</c> with one result per tag, or a failure.</param>
public sealed record GitWriterTagsResult(string Outcome, string? Reason, IReadOnlyList<GitWriterTagResult>? Tags, string? Detail, long DurationMs)
{
    public const string Tagged = "tagged";
}

/// <param name="Outcome"><c>created</c>, <c>already-present</c>, <c>conflict</c> or <c>unreachable</c>.</param>
public sealed record GitWriterTagResult(string Name, string Outcome, string? Detail)
{
    public const string Created = "created";
    public const string AlreadyPresent = "already-present";
}

/// <summary>
/// The body of the git writer's <c>POST /jobs/versioning/backfill</c> (#638 D5-D8): every version the default branch's and the
/// release lines' history declares, tagged at its earliest occurrence on the line of its major.
/// </summary>
public sealed record GitWriterBackfillRequest(
    string Provider, string RepoId, string CloneUrl, SidecarCredential Credential, string DefaultBranch, GitWriterTagger Tagger, VersioningSettings Settings,
    bool PlanOnly = false, IReadOnlyList<string>? Move = null);

/// <summary>#638 D9: an App-made tag on <paramref name="Current"/> whose version the history declares first at <paramref name="Commit"/>.</summary>
/// <param name="Outcome"><c>planned</c>, <c>left</c>, <c>moved</c> or <c>conflict</c> (the tag changed since the job read it).</param>
public sealed record GitWriterBackfillCollision(string Name, string Current, string Commit, string Branch, string Outcome, string? Detail)
{
    public const string Moved = "moved";
}

/// <param name="Outcome"><c>created</c>, <c>already-present</c>, <c>conflict</c>, <c>unreachable</c> or <c>planned</c>.</param>
public sealed record GitWriterBackfillTag(string Name, string Commit, string Branch, string Outcome, string? Detail);

/// <param name="Outcome"><see cref="Planned"/>, <see cref="Tagged"/>, or a failure.</param>
public sealed record GitWriterBackfillResult(string Outcome, string? Reason, IReadOnlyList<GitWriterBackfillTag>? Tags, IReadOnlyList<string>? Branches,
    IReadOnlyList<string>? Notes, string? Detail, long DurationMs)
{
    public const string Tagged = "tagged";
    public const string Planned = "planned";

    public IReadOnlyList<GitWriterBackfillCollision>? Collisions { get; init; }
}

/// <summary>The writer's answer: always 200 with an outcome, as every job (<c>GitWriterOutcomes</c>).</summary>
public sealed record GitWriterVersioningResult(
    string Outcome, string? Reason, IReadOnlyList<GitWriterRelease>? Releases, IReadOnlyList<GitWriterPackageNote>? Skipped,
    IReadOnlyList<GitWriterPackageNote>? Refused, IReadOnlyList<string>? Warnings, string? Detail, long DurationMs,
    IReadOnlyList<GitWriterBranchCut>? BranchCuts = null, TagPlan? Plan = null)
{
    public const string Versioned = "versioned";
    public const string PlanRefused = "plan-refused";

    /// <summary>On a release line, the major of each package anchored on its platform (#638 D2).</summary>
    public IReadOnlyDictionary<string, int>? LineMajors { get; init; }

    /// <summary>The writer's reason for a run the plan could not be made for (a missing package, a commit off the branch).</summary>
    public const string Unplannable = "unplannable";
}

public sealed record GitWriterRelease(string Id, string Kind, string Version, string Tag, IReadOnlyList<string>? Reasons, string? ApiHash, bool TaggedByHand, string? TagOutcome,
    GitWriterReleaseNotes? Release = null);

/// <summary>The GitHub Release the app creates for a tag (D15).</summary>
public sealed record GitWriterReleaseNotes(string Name, string Body, bool Prerelease);

public sealed record GitWriterPackageNote(string Id, string Reason);

/// <summary>A release branch a platform major move asks for (D12): <see cref="Branch"/> at the release commit's first parent.</summary>
public sealed record GitWriterBranchCut(string Branch, int Major, string Commit, IReadOnlyList<string>? Packages, int? PullRequest, string? Ecosystem = null);

/// <summary><c>POST jobs/versioning/plan</c> (#153 M11, D3 trigger B): would a run on <see cref="Commit"/> release anything?</summary>
public sealed record GitWriterVersioningPlanRequest(
    string Provider, string RepoId, string CloneUrl, SidecarCredential Credential, string Branch, string DefaultBranch, string Commit, int? BranchMajor,
    VersioningSettings Settings);

/// <summary>The writer's answer. <see cref="Releases"/> holds package ids only: the plan has no API hashes, so its versions are not the run's.</summary>
public sealed record GitWriterVersioningPlanResult(
    string Outcome, string? Reason, IReadOnlyList<string>? Releases, IReadOnlyList<GitWriterPackageNote>? Refused, IReadOnlyList<string>? Warnings,
    string? Detail, long DurationMs)
{
    public const string Planned = "planned";
}

/// <summary><c>POST jobs/versioning/ecosystems</c> (#570 M8b, D5): the ecosystems a pull request touches.</summary>
/// <param name="HeadRef">Where the head is fetched from, <c>refs/pull/N/head</c>.</param>
public sealed record GitWriterEcosystemsRequest(
    string Provider, string RepoId, string CloneUrl, SidecarCredential Credential, string BaseBranch, string HeadRef, string HeadSha, VersioningSettings Settings);

/// <param name="Outcome"><see cref="Planned"/>, <c>head-mismatch</c> when the pull request moved on, or a failure.</param>
public sealed record GitWriterEcosystemsResult(string Outcome, string? Reason, IReadOnlyList<string>? Ecosystems, string? Detail, long DurationMs)
{
    public const string Planned = "planned";
}

/// <summary><c>POST jobs/versioning/promote</c> (segment P): release lines moved to a green default-branch commit.</summary>
public sealed record GitWriterPromoteRequest(
    string Provider, string RepoId, string CloneUrl, SidecarCredential Credential, string DefaultBranch, string Commit, GitWriterTagger Committer,
    IReadOnlyList<GitWriterPromoteLine> Lines, VersioningSettings Settings, IReadOnlyList<GitWriterPromoteVersion> Versions);

public sealed record GitWriterPromoteLine(string Branch, string Ecosystem, int Major, bool OnlyIfChanged);

/// <summary><c>POST jobs/versioning/hand-tag</c> (segment P, P5; segment T): a person's new tag, checked against the tag it follows.</summary>
/// <param name="Lines">Every release line, current and past (their <c>OnlyIfChanged</c> is ignored).</param>
/// <param name="Promotes"><c>versioning.lines.current: promote</c>: a tag of a current line's major is recorded for its next promotion.</param>
public sealed record GitWriterHandTagRequest(
    string Provider, string RepoId, string CloneUrl, SidecarCredential Credential, string Tag, string TagObject, IReadOnlyList<GitWriterPromoteLine> Lines,
    bool Promotes, VersioningSettings Settings);

/// <param name="Tag">One of the constants below, when <see cref="Outcome"/> is <see cref="Planned"/>.</param>
public sealed record GitWriterHandTagResult(string Outcome, string? Reason, string? Tag, string? Branch, string? Commit, string? Detail, long DurationMs)
{
    public const string Planned = "planned";
    public const string Recorded = "recorded";
    /// <summary>Valid and left in place (segment T): a past line's tag, or any line's in a repository that backports.</summary>
    public const string Accepted = "accepted";
    public const string Refused = "refused";
    public const string NotAPackage = "not-a-package";
    public const string Moved = "moved";
}

/// <summary>A version to release (P5): tagged <c>&lt;PackageId&gt;/v&lt;Version&gt;</c> on its line's promotion commit.</summary>
/// <param name="Recorded">A version recorded earlier from a hand tag: when it no longer fits, the writer reports it instead of refusing its line.</param>
public sealed record GitWriterPromoteVersion(string PackageId, string Version, bool Recorded = false);

/// <param name="Outcome"><see cref="Promoted"/> when the job ran (each line then has its own outcome), or a failure.</param>
/// <param name="Unplaced">Versions no line of the request releases, with the reason.</param>
public sealed record GitWriterPromoteResult(string Outcome, string? Reason, IReadOnlyList<GitWriterPromoteLineResult>? Lines, string? Detail, long DurationMs,
    IReadOnlyList<GitWriterPromoteUnplaced>? Unplaced = null)
{
    public const string Promoted = "promoted";
}

public sealed record GitWriterPromoteUnplaced(string PackageId, string Version, string Detail);

/// <param name="Outcome">One of the constants below.</param>
/// <param name="Tags">For <see cref="Promoted"/>: the version tags pushed with the line.</param>
public sealed record GitWriterPromoteLineResult(string Branch, string Outcome, string Commit, IReadOnlyList<string>? Paths, IReadOnlyList<string>? Commits, string? Detail = null,
    IReadOnlyList<string>? Tags = null)
{
    public const string Promoted = "promoted";
    public const string Contained = "contained";
    public const string Unchanged = "unchanged";
    public const string LineOnly = "line-only";
    public const string VersionRefused = "version-refused";
    public const string Moved = "moved";
    public const string Rejected = "rejected";
}

/// <summary><c>POST jobs/versioning/backport</c> (#153 M10, D10): the merged pull request applied to release branches.</summary>
public sealed record GitWriterBackportRequest(
    string Provider, string RepoId, string CloneUrl, SidecarCredential Credential, string SourceBranch, string Commit, int PullRequest, string Title,
    IReadOnlyList<string> Subjects, GitWriterTagger Committer, IReadOnlyList<GitWriterBackportTarget> Targets, PlatformMarkerOptions? Markers = null);

/// <param name="AutoResolve">The merge queue's rules for this line's conflicts (#570 M5); stage 2 is the line.</param>
/// <param name="Draft"><c>backport.onConflict: draft</c> (#570 D7): a conflict is pushed with its markers.</param>
/// <param name="RegeneratedPaths">The derived files a backport never merges (#570 D9, M6,
/// <c>BackportRecipient.LineSidePaths</c>): a path the change touches that matches one keeps the line's side and is
/// listed in <see cref="GitWriterBackportTargetResult.Regenerate"/>. Not the whole of the queue's <c>regeneratedPaths</c>,
/// which holds hand-written files too.</param>
public sealed record GitWriterBackportTarget(string Branch, string Head, IReadOnlyList<GitWriterAutoResolve>? AutoResolve = null, bool Draft = false,
    IReadOnlyList<string>? RegeneratedPaths = null);

/// <summary>The writer's answer. <see cref="Backported"/> when the job ran; each target then has its own outcome
/// (#570 D6).</summary>
public sealed record GitWriterBackportResult(
    string Outcome, string? Reason, string? Mode, string? Base, IReadOnlyList<GitWriterBackportTargetResult>? Targets, string? Detail, long DurationMs)
{
    public const string Backported = "backported";
}

/// <param name="Push"><c>created</c>, <c>exists</c>, <c>empty</c> (the line already holds the change), <c>rejected</c>
/// <c>conflicted</c> (nothing pushed to this line) or <c>drafted</c> (pushed with its conflict markers, #570 D7).</param>
/// <param name="PlatformChanges">Platform values the change edits on this line's own platform (#570 S2, D8).</param>
/// <param name="Resolutions">Conflicts the rules resolved; a <c>regenerate</c> one still needs the line's workflow (M6).</param>
/// <param name="Regenerate">The paths the line's regenerate workflow must rewrite (#570 D9, M6): the regenerated paths the
/// change touches, kept at the line's side, and the <c>regenerate</c> resolutions.</param>
public sealed record GitWriterBackportTargetResult(string Branch, string Head, string? Commit, string? Push, IReadOnlyList<string>? Conflicts, string? Detail = null,
    IReadOnlyList<GitWriterPlatformChange>? PlatformChanges = null, IReadOnlyList<GitWriterBackportResolution>? Resolutions = null,
    IReadOnlyList<string>? Regenerate = null)
{
    /// <summary>First-party packages the change raised above this line's major, dropped by normalization (#570 D11).</summary>
    public IReadOnlyList<GitWriterDependencyRaise>? Raises { get; init; }

    public const string Created = "created";
    public const string Exists = "exists";
    public const string Empty = "empty";
    public const string Conflicted = "conflicted";
    public const string Drafted = "drafted";
}

/// <summary><c>POST jobs/versioning/backport/trial</c> (segment G): <paramref name="Base"/>..<paramref name="Commit"/> tried on
/// each line before the merge, nothing pushed. <paramref name="HeadRef"/> is <c>refs/pull/N/head</c>, at <paramref name="Commit"/>.</summary>
public sealed record GitWriterBackportTrialRequest(
    string Provider, string RepoId, string CloneUrl, SidecarCredential Credential, string HeadRef, string Base, string Commit,
    IReadOnlyList<GitWriterBackportTrialTarget> Targets, PlatformMarkerOptions? Markers = null);

/// <summary>A line to try, with the backport's own rules (<see cref="GitWriterBackportTarget"/>).</summary>
public sealed record GitWriterBackportTrialTarget(string Branch, IReadOnlyList<GitWriterAutoResolve>? AutoResolve = null, IReadOnlyList<string>? RegeneratedPaths = null);

/// <summary>The writer's answer: <see cref="Tried"/> when the job ran, each line then with its own outcome.</summary>
public sealed record GitWriterBackportTrialResult(string Outcome, string? Reason, IReadOnlyList<GitWriterBackportTrialTargetResult>? Targets, string? Detail, long DurationMs)
{
    public const string Tried = "tried";
}

/// <param name="Outcome"><see cref="Clean"/>, <see cref="Empty"/> (the line already holds it) or <see cref="Conflicted"/>.</param>
public sealed record GitWriterBackportTrialTargetResult(string Branch, string Outcome, IReadOnlyList<string>? Conflicts, string? Detail = null)
{
    public const string Clean = "clean";
    public const string Empty = "empty";
    public const string Conflicted = "conflicted";
}

/// <summary>One platform value the change edits on the line's platform; <see cref="Before"/> is empty for an added one.</summary>
public sealed record GitWriterPlatformChange(string Path, string Marker, string Before, string After);

/// <summary>A first-party package the change raised that the line keeps at <see cref="LineValue"/> (#570 D11);
/// <see cref="Kind"/> is <c>npm</c> or <c>nuget</c>.</summary>
public sealed record GitWriterDependencyRaise(string Path, string Kind, string Package, string Before, string After, string LineValue);

/// <summary>One conflict resolved on a line by an <c>autoResolve</c> rule.</summary>
public sealed record GitWriterBackportResolution(string Path, string Strategy, bool Regenerate, int Rule);

/// <summary>
/// <c>POST jobs/versioning/backport/regenerated</c> (#570 M6, D9): the line's regenerate workflow ran on
/// <see cref="Head"/> at <see cref="ExpectedSha"/>; its patch (base64, empty for none) is folded into the backport commit.
/// </summary>
public sealed record GitWriterBackportRegeneratedRequest(
    string Provider, string RepoId, string CloneUrl, SidecarCredential Credential, string Head, string ExpectedSha, string Patch,
    IReadOnlyList<string> RegeneratedPaths, GitWriterTagger Committer);

/// <param name="Outcome"><see cref="Regenerated"/> (<see cref="Commit"/> is the branch's new head), <c>patch-rejected</c>,
/// <c>branch-moved</c>, <c>push-rejected</c>, or a job failure.</param>
/// <param name="Paths">The paths the patch changed, or the ones it was refused for.</param>
public sealed record GitWriterBackportRegeneratedResult(string Outcome, string? Reason, string? Commit, IReadOnlyList<string>? Paths, string? Detail, long DurationMs)
{
    public const string Regenerated = "regenerated";
}
