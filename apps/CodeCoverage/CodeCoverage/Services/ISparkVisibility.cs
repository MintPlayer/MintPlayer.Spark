namespace CodeCoverage.Services;

/// <summary>
/// Per-request visibility snapshots for the Spark row-security hooks. The hooks run
/// up to three times per detail read (once per action) plus once per save, and the
/// redaction hook runs per row — so every answer here is memoized for the request.
/// The underlying owner list is additionally cached ~5 minutes per user by
/// <see cref="Forge.IForgeIntegrationResolver"/>, fanned out across every forge the viewer is
/// signed in to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two questions, never one (dependency-updates PRD D-O12, owner decision R39).</b> "May see" is the owner set: every
/// owner whose installation the viewer can reach, which on GitHub includes read access to a single repository. It decides
/// which private repositories, coverage and commits a viewer sees, and nothing else. "Manages" is the tight rule, and this
/// interface is its single definition for the whole app: an organization is managed only by its active owners, a personal
/// account only by that user (<see cref="CanManageOwnerAsync"/>, asked of the forge); a repository additionally by an
/// administrator of that one repository (<see cref="CanManageRepositoryAsync"/>). Settings visibility, Edit, the write row
/// filters, tokens, boards and dependency-update enablement all ask the "manages" questions.
/// </para>
/// </remarks>
public interface ISparkVisibility
{
    /// <summary>Owners the current viewer may SEE (private repositories included); empty for anonymous viewers. Not a
    /// management right.</summary>
    Task<string[]> GetAllowedOwnersAsync();

    /// <summary>
    /// Document ids of repositories the current viewer may see (public ones plus
    /// those of GitHub-granted owners). Feeds the Commit row filter as an IN list
    /// and the Build per-row check.
    /// </summary>
    Task<string[]> GetVisibleRepositoryIdsAsync();

    /// <summary>
    /// Whether the viewer manages this owner (account): its active organization owner, or the personal account's own user
    /// (R39). Gates account settings visibility, account Edit, tokens, boards and the dependency-update account reason.
    /// </summary>
    /// <param name="ownerKey">A <c>provider:login</c> key, not a bare login.</param>
    Task<bool> CanManageOwnerAsync(string ownerKey);

    /// <summary>The owner keys the viewer manages (<see cref="CanManageOwnerAsync"/> over the owner set): the write row filters' IN list.</summary>
    Task<string[]> GetManagedOwnersAsync();

    /// <summary>
    /// Whether the viewer manages this repository's settings: a manager of its account (<see cref="CanManageOwnerAsync"/>),
    /// or an administrator of this one repository on its forge (R33's top role, cached like the owner set). Gates the
    /// repository's settings visibility (badge token included), its Edit and its check-exclusion picker.
    /// </summary>
    Task<bool> CanManageRepositoryAsync(Entities.Repository repository);

    /// <summary>
    /// Document ids of the repositories the viewer administers in accounts they can reach but do not manage: the second
    /// half of the Repository write row filter (the first is <see cref="GetManagedOwnersAsync"/>).
    /// </summary>
    Task<string[]> GetAdministeredRepositoryIdsAsync();
}
