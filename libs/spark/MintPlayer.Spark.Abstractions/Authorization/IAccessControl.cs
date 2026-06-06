namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// Interface for access control services. When registered, Spark will check
/// permissions before allowing CRUD operations on PersistentObjects and Queries.
/// </summary>
public interface IAccessControl
{
    /// <summary>
    /// Checks if the current user has permission to perform an action on a resource.
    /// </summary>
    /// <param name="resource">The resource identifier. Format examples:
    /// <list type="bullet">
    /// <item><description>"Read/Person" - Read access to Person PersistentObject</description></item>
    /// <item><description>"Edit/Person" - Edit access to Person PersistentObject</description></item>
    /// <item><description>"Query/Person" - Query/list access to Person PersistentObject</description></item>
    /// </list>
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if access is allowed, false otherwise</returns>
    Task<bool> IsAllowedAsync(string resource, CancellationToken cancellationToken = default);
}
