using CodeCoverage.CommitGraph;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// The body of the git writer's <c>POST /jobs/versioning/run</c> (#153 M6). The settings and the manifest are the engine's
/// own types, so what the writer reads is exactly what the app validated.
/// </summary>
public sealed record GitWriterVersioningRequest(
    string Provider, string RepoId, string CloneUrl, SidecarCredential Credential, string Branch, string DefaultBranch, int? BranchMajor,
    GitWriterTagger Tagger, VersioningSettings Settings, ReleaseManifest Manifest);

public sealed record GitWriterTagger(string Name, string Email);

/// <summary>The writer's answer: always 200 with an outcome, as every job (<c>GitWriterOutcomes</c>).</summary>
public sealed record GitWriterVersioningResult(
    string Outcome, string? Reason, IReadOnlyList<GitWriterRelease>? Releases, IReadOnlyList<GitWriterPackageNote>? Skipped,
    IReadOnlyList<GitWriterPackageNote>? Refused, IReadOnlyList<string>? Warnings, string? Detail, long DurationMs,
    IReadOnlyList<GitWriterBranchCut>? BranchCuts = null)
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
public sealed record GitWriterBranchCut(string Branch, int Major, string Commit, IReadOnlyList<string>? Packages, int? PullRequest);
