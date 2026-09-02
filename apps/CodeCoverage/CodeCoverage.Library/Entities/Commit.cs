using MintPlayer.Spark.Abstractions;
using Newtonsoft.Json;

namespace CodeCoverage.Entities;

/// <summary>
/// A commit we have seen (via push/pull_request webhooks or a coverage upload).
/// Document id is Commits/{repoGitHubId}/{sha} so any source can upsert idempotently.
/// </summary>
public class Commit
{
    public string? Id { get; set; }

    [Reference(typeof(Repository))]
    public string? Repository { get; set; }

    public string Sha { get; set; } = string.Empty;

    public string? Branch { get; set; }

    public int? PullRequestNumber { get; set; }

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

    public string? Message { get; set; }

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

    /// <summary>Merged coverage of the latest finalized build, denormalized for lists/badges.</summary>
    public CoverageSummary? Coverage { get; set; }

    /// <summary>
    /// Line-coverage change in percentage points versus the chronologically
    /// previous commit that has coverage. Computed per request by the commits
    /// query over the whole ordered sequence and never persisted — a
    /// list-relative number is meaningless outside the list that produced it.
    /// (A commit-graph delta would need an explicit base sha: ParentSha means
    /// two different things depending on which writer set it — see
    /// docs/roadmap-2026-08.md §7, T2.1.)
    /// <para>
    /// [JsonIgnore] keeps it out of the stored document — RavenDB serializes
    /// through Newtonsoft, so this is the mechanism, not a Spark concern. It
    /// stays a normal model attribute: the grid's Δ column reads it.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public double? CoverageDelta { get; set; }

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
