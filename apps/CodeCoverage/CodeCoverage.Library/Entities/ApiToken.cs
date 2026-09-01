namespace CodeCoverage.Entities;

/// <summary>
/// An upload credential for CI. The document id is ApiTokens/{sha256-hex of
/// the token value}, so global uniqueness holds by construction and lookup is
/// a point-load; the plaintext value is shown once at creation and never stored.
///
/// Deliberately app-local: the planned extraction into a generic Spark
/// ApiTokens library was cancelled upstream in favor of client_credentials
/// (docs/PRD.md §10) — Coverage keeps its own covt_ tokens.
/// </summary>
public class ApiToken
{
    public string? Id { get; set; }

    /// <summary>"Account" (all repos of a user/org) or "Repository" (one repo).</summary>
    public string Scope { get; set; } = "Account";

    /// <summary>Owner login this token uploads for, when Scope is "Account".</summary>
    public string? AccountLogin { get; set; }

    /// <summary>GitHub repository id this token uploads for, when Scope is "Repository".</summary>
    public long? RepositoryGitHubId { get; set; }

    public string? Description { get; set; }

    public string CreatedByUserId { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    public static string DocumentId(string tokenHash) => $"ApiTokens/{tokenHash}";
}
