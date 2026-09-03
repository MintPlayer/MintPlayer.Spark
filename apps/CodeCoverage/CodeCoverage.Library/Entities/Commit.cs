using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// A commit we have seen (via push/pull_request webhooks or a coverage upload).
/// Document id is Commits/{repoGitHubId}/{sha} so any source can upsert idempotently.
/// </summary>
public class Commit
{
    /// <summary>Document id of this commit, <c>Commits/{repoGitHubId}/{sha}</c>.</summary>
    public string? Id { get; set; }

    /// <summary>The repository this commit belongs to.</summary>
    [Reference(typeof(Repository))]
    public string? Repository { get; set; }

    /// <summary>The full 40-character git commit sha.</summary>
    public string Sha { get; set; } = string.Empty;

    /// <summary>The branch the commit was pushed to or the PR head branch, when known.</summary>
    public string? Branch { get; set; }

    /// <summary>Number of the pull request this commit was seen on; null for plain pushes.</summary>
    public int? PullRequestNumber { get; set; }

    /// <summary>
    /// The branch the pull request TARGETS — <c>main</c>, not the head branch in
    /// <see cref="Branch"/>. Null for pushes, and for PR commits recorded before
    /// this field existed.
    /// <para>
    /// Written authoritatively by the pull_request webhook and best-effort
    /// (<c>??=</c>) by the upload, matching how Branch and PullRequestNumber are
    /// handled. A PR's target can be retargeted, in which case the webhook's
    /// later write wins.
    /// </para>
    /// </summary>
    public string? PullRequestBaseRef { get; set; }

    /// <summary>
    /// Tip of <see cref="PullRequestBaseRef"/> as of the PR's last synchronise.
    /// <para>
    /// NOT the same thing as <c>Build.DeclaredBaseSha</c>: that is the caller's
    /// declared affected-computation base (this repo passes nx's NX_BASE) and is
    /// not guaranteed to be the merge-base. Patch coverage deliberately still
    /// diffs against DeclaredBaseSha ?? ParentSha — repointing a shipped number
    /// belongs to the honest-numbers work, not here.
    /// </para>
    /// </summary>
    public string? PullRequestBaseSha { get; set; }

    /// <summary>Sha of the git first parent, used as the reference for the delta-vs-parent; see the source field for how much to trust it.</summary>
    public string? ParentSha { get; set; }

    /// <summary>
    /// Who set <see cref="ParentSha"/>: <c>upload</c> (the action's claim) or
    /// <c>api</c> (verified against GitHub's commits API). Only <c>api</c> is
    /// trusted for the Δ-vs-parent; older action builds sent the PR base sha
    /// under the same name.
    /// </summary>
    public string? ParentShaSource { get; set; }

    /// <summary>
    /// When the server last asked GitHub for the parent (whether or not it
    /// answered). The backfill job walks commits where this is null, so a
    /// repository without any API path is asked once, not forever.
    /// </summary>
    public DateTime? ParentLookupAttemptedAtUtc { get; set; }

    /// <summary>The commit message, as delivered by the push or pull-request webhook.</summary>
    public string? Message { get; set; }

    /// <summary>When the commit was authored, from the webhook payload; null for commits only seen through an upload.</summary>
    public DateTimeOffset? AuthoredAt { get; set; }

    /// <summary>
    /// When this document was first created, by whichever path saw the commit
    /// first (webhook or upload). AuthoredAt only arrives via push/PR webhooks,
    /// so upload-only commits (the norm for OIDC auto-provisioned repos) would
    /// otherwise have nothing to sort by — lists order by AuthoredAt coalesced
    /// with this.
    /// </summary>
    public DateTimeOffset? FirstSeenAtUtc { get; set; }

    /// <summary>
    /// The date to show for this commit: when it was authored, falling back to
    /// when we first saw it (upload-only commits have no AuthoredAt). Get-only,
    /// so Spark serves it as a plain attribute — the grids sort and render this
    /// rather than the two half-populated fields behind it.
    /// </summary>
    public DateTimeOffset? Date => AuthoredAt ?? FirstSeenAtUtc;

    /// <summary>
    /// The commit's headline: the assembled coverage (every finalized build of
    /// the commit unioned, plus files carried unchanged from the base), stamped
    /// by the assembler and denormalized for lists and badges.
    /// </summary>
    public CoverageSummary? Coverage { get; set; }

    /// <summary>
    /// Percentage-point change versus the git first parent (<see cref="ParentSha"/>),
    /// stamped by the assembler. Null when the parent has no coverage — rendered
    /// as "—", never as a fake zero.
    /// </summary>
    public double? CoverageDeltaVsParent { get; set; }

    /// <summary>
    /// Percentage-point change versus the default branch's newest complete
    /// coverage at or before this commit's date — "what would merging this do to
    /// the headline". Equals <see cref="CoverageDeltaVsParent"/> on the default
    /// branch itself. Null when there is no such reference.
    /// </summary>
    public double? CoverageDeltaVsDefaultBranch { get; set; }

    /// <summary>
    /// <see cref="CommitAssembly.Complete"/> / <see cref="CommitAssembly.Partial"/>
    /// copied from the assembly so lists and indexes can filter without loading
    /// it. Null for commits whose coverage predates assemblies (full uploads,
    /// treated as complete).
    /// </summary>
    public string? AssemblyCompleteness { get; set; }

    /// <summary>The build whose coverage is shown for this commit (file tree reads its FileCoverage docs).</summary>
    public string? LatestBuildId { get; set; }

    public static string DocumentId(long repoGitHubId, string sha) => $"Commits/{repoGitHubId}/{sha}";
}
