namespace WebhooksDemo.Services;

public interface IOrganizationAccessService
{
    /// <summary>
    /// Returns the GitHub owner logins (org logins + personal username) the current
    /// authenticated user is allowed to access. Queried live from GitHub via the user's
    /// stored OAuth access token and cached for the duration of the request.
    /// Returns empty if the user is not authenticated or the GitHub query fails.
    /// </summary>
    Task<string[]> GetAllowedOwnersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true if the current user is allowed to access entities owned by the given owner login.
    /// </summary>
    Task<bool> IsOwnerAllowedAsync(string ownerLogin, CancellationToken cancellationToken = default);
}
