using CodeCoverage.CommitGraph;
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

/// <summary>The writer's answer: always 200 with an outcome, as every job (<c>GitWriterOutcomes</c>).</summary>
public sealed record GitWriterVersioningResult(
    string Outcome, string? Reason, IReadOnlyList<GitWriterRelease>? Releases, IReadOnlyList<GitWriterPackageNote>? Skipped,
    IReadOnlyList<GitWriterPackageNote>? Refused, IReadOnlyList<string>? Warnings, string? Detail, long DurationMs,
    IReadOnlyList<GitWriterBranchCut>? BranchCuts = null, TagPlan? Plan = null)
{
    public const string Versioned = "versioned";
    public const string PlanRefused = "plan-refused";

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

/// <summary><c>POST jobs/versioning/backport</c> (#153 M10, D10): the merged pull request applied to release branches.</summary>
public sealed record GitWriterBackportRequest(
    string Provider, string RepoId, string CloneUrl, SidecarCredential Credential, string SourceBranch, string Commit, int PullRequest, string Title,
    IReadOnlyList<string> Subjects, GitWriterTagger Committer, IReadOnlyList<GitWriterBackportTarget> Targets);

/// <param name="AutoResolve">The merge queue's rules for this line's conflicts (#570 M5); stage 2 is the line.</param>
public sealed record GitWriterBackportTarget(string Branch, string Head, IReadOnlyList<GitWriterAutoResolve>? AutoResolve = null);

/// <summary>The writer's answer. <see cref="Backported"/> when the job ran; each target then has its own outcome
/// (#570 D6).</summary>
public sealed record GitWriterBackportResult(
    string Outcome, string? Reason, string? Mode, string? Base, IReadOnlyList<GitWriterBackportTargetResult>? Targets, string? Detail, long DurationMs)
{
    public const string Backported = "backported";
}

/// <param name="Push"><c>created</c>, <c>exists</c>, <c>empty</c> (the line already holds the change), <c>rejected</c>
/// or <c>conflicted</c> (nothing pushed to this line).</param>
/// <param name="PlatformChanges">Platform values the change edits on this line's own platform (#570 S2, D8).</param>
/// <param name="Resolutions">Conflicts the rules resolved; a <c>regenerate</c> one still needs the line's workflow (M6).</param>
public sealed record GitWriterBackportTargetResult(string Branch, string Head, string? Commit, string? Push, IReadOnlyList<string>? Conflicts, string? Detail = null,
    IReadOnlyList<GitWriterPlatformChange>? PlatformChanges = null, IReadOnlyList<GitWriterBackportResolution>? Resolutions = null)
{
    public const string Created = "created";
    public const string Exists = "exists";
    public const string Empty = "empty";
    public const string Conflicted = "conflicted";
}

/// <summary>One platform value the change edits on the line's platform; <see cref="Before"/> is empty for an added one.</summary>
public sealed record GitWriterPlatformChange(string Path, string Marker, string Before, string After);

/// <summary>One conflict resolved on a line by an <c>autoResolve</c> rule.</summary>
public sealed record GitWriterBackportResolution(string Path, string Strategy, bool Regenerate, int Rule);
