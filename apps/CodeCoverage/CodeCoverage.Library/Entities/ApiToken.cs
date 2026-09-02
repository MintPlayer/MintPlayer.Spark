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
    /// <summary>Document id of this token, <c>ApiTokens/{sha256 of the token value}</c>; the plaintext token itself is never stored.</summary>
    public string? Id { get; set; }

    /// <summary>"Account" (all repos of a user/org) or "Repository" (one repo).</summary>
    public string Scope { get; set; } = "Account";

    /// <summary>Owner login this token uploads for, when Scope is "Account".</summary>
    public string? AccountLogin { get; set; }

    /// <summary>GitHub repository id this token uploads for, when Scope is "Repository".</summary>
    public long? RepositoryGitHubId { get; set; }

    /// <summary>Free-text label telling you where this token is used, e.g. the CI workflow it was created for.</summary>
    public string? Description { get; set; }

    /// <summary>Id of the signed-in user who created this token.</summary>
    public string CreatedByUserId { get; set; } = string.Empty;

    /// <summary>When the token was created (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the token was revoked (UTC); null while it is still valid for uploads.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    public static string DocumentId(string tokenHash) => $"ApiTokens/{tokenHash}";
}
