namespace CodeCoverage.Services;

/// <summary>
/// Per-request visibility snapshots for the Spark row-security hooks. The hooks run
/// up to three times per detail read (once per action) plus once per save, and the
/// redaction hook runs per row — so every answer here is memoized for the request.
/// The underlying owner list is additionally cached ~5 minutes per user by
/// <see cref="IGitHubAccessService"/>.
/// </summary>
public interface ISparkVisibility
{
    /// <summary>Owners the current viewer may see; empty for anonymous viewers.</summary>
    Task<string[]> GetAllowedOwnersAsync();

    /// <summary>
    /// Document ids of repositories the current viewer may see (public ones plus
    /// those of GitHub-granted owners). Feeds the Commit row filter as an IN list
    /// and the Build per-row check.
    /// </summary>
    Task<string[]> GetVisibleRepositoryIdsAsync();

    /// <summary>Whether the viewer manages this owner (gates BadgeToken/InstallationId visibility).</summary>
    Task<bool> CanManageOwnerAsync(string ownerLogin);
}
