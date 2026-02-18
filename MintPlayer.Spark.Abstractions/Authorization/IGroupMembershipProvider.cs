namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// Interface for resolving user group memberships.
/// Implement this to integrate with your authentication system (ASP.NET Identity, OAuth, etc.).
/// </summary>
public interface IGroupMembershipProvider
{
    /// <summary>
    /// Gets the group names for the current user.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of group names the current user belongs to</returns>
    Task<IEnumerable<string>> GetCurrentUserGroupsAsync(CancellationToken cancellationToken = default);
}
