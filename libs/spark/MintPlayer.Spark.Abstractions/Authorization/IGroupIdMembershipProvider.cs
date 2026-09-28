namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// Implemented <em>alongside</em> <see cref="IGroupMembershipProvider"/> by a provider that knows
/// the caller's groups by <b>id</b> — the keys of <c>security.json</c>'s <c>groups</c> block — rather
/// than, or as well as, by display name (#460, D12).
/// </summary>
/// <remarks>
/// <para>
/// A separate interface rather than a naming convention inside the name list: a group id and a
/// display name are both strings, and an application is free to name a group with anything —
/// including something that parses as a GUID — so a prefix or a "looks like a GUID" rule would be a
/// guess. A provider that implements this states unambiguously which values are ids. One that knows
/// only ids returns no names from <see cref="IGroupMembershipProvider.GetCurrentUserGroupsAsync"/>.
/// </para>
/// <para>
/// Ids get the same treatment as resolved names: an id declared in <c>wellKnown</c> is dropped
/// (<c>anonymous</c> and <c>authenticated</c> are decided from authentication state, never asserted),
/// and an id <c>security.json</c> does not declare grants nothing.
/// </para>
/// </remarks>
public interface IGroupIdMembershipProvider
{
    /// <summary>The ids of the groups the current caller belongs to.</summary>
    Task<IEnumerable<Guid>> GetCurrentUserGroupIdsAsync(CancellationToken cancellationToken = default);
}
