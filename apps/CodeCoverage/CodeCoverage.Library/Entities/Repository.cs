using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// A GitHub repository the app knows about (installed via the GitHub App).
/// Document id is Repositories/{GitHubId} so webhook upserts are idempotent.
/// </summary>
[GenerateIndex]
public class Repository
{
    /// <summary>Document id of this repository, <c>Repositories/{GitHubId}</c>.</summary>
    public string? Id { get; set; }

    /// <summary>The GitHub user or organization that owns this repository.</summary>
    [Reference(typeof(Account))]
    public string? Account { get; set; }

    /// <summary>GitHub's numeric id for this repository; stable across renames and transfers.</summary>
    public long GitHubId { get; set; }

    /// <summary>The repository name without the owner, e.g. <c>MintPlayer.Spark</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>owner/name</summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>GitHub login of the owning user or organization, the part before the slash in the full name.</summary>
    public string OwnerLogin { get; set; } = string.Empty;

    /// <summary>True when the repository is private on GitHub; private repositories need a badge token for their badge.</summary>
    public bool IsPrivate { get; set; }

    /// <summary>The repository's default branch on GitHub (e.g. <c>master</c>); its newest finalized build supplies the headline coverage.</summary>
    public string? DefaultBranch { get; set; }

    /// <summary>True when the repository has been archived on GitHub and no longer receives uploads.</summary>
    public bool Archived { get; set; }

    /// <summary>
    /// Grants access to the rendered badge SVG only — never report data.
    /// Set for private repositories; independently rotatable.
    ///
    /// [IgnoreForIndex] because index membership is opt-out: without it this
    /// lands in VRepository, and synchronize then marks every projected field
    /// queryable — putting a live badge token in the /spark repository grid,
    /// which security.json grants to Everyone. Nothing filters or sorts on it,
    /// so the index has no use for it either.
    /// </summary>
    [IgnoreForIndex]
    public string? BadgeToken { get; set; }

    /// <summary>
    /// Gate policy; null means every default (informational, auto-ratchet).
    /// [IgnoreForIndex]: policy is owner-facing configuration — it has no
    /// business in the anonymous /spark grid, and nothing filters on it.
    /// </summary>
    [IgnoreForIndex]
    public GateSettings? Gate { get; set; }

    /// <summary>
    /// Denormalized from the newest finalized default-branch build, so repo
    /// lists and badges are point-loads.
    /// </summary>
    public CoverageSummary? LatestCoverage { get; set; }

    /// <summary>Sha of the default-branch commit the headline coverage was taken from.</summary>
    public string? LatestCoverageSha { get; set; }

    /// <summary>When the headline coverage was last refreshed from a finalized default-branch build (UTC).</summary>
    public DateTime? LatestCoverageAtUtc { get; set; }

    public static string DocumentId(long gitHubId) => $"Repositories/{gitHubId}";
}
