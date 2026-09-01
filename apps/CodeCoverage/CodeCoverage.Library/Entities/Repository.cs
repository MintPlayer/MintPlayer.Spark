using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// A GitHub repository the app knows about (installed via the GitHub App).
/// Document id is Repositories/{GitHubId} so webhook upserts are idempotent.
/// </summary>
[GenerateIndex]
public class Repository
{
    public string? Id { get; set; }

    [Reference(typeof(Account))]
    public string? Account { get; set; }

    public long GitHubId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>owner/name</summary>
    public string FullName { get; set; } = string.Empty;

    public string OwnerLogin { get; set; } = string.Empty;

    public bool IsPrivate { get; set; }

    public string? DefaultBranch { get; set; }

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

    public string? LatestCoverageSha { get; set; }

    public DateTime? LatestCoverageAtUtc { get; set; }

    public static string DocumentId(long gitHubId) => $"Repositories/{gitHubId}";
}
