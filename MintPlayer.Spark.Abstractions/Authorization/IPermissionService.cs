namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// High-level authorization service used by Spark components to check permissions.
/// Wraps IAccessControl (when registered) and provides convenience methods
/// that build resource strings and throw on denial.
/// </summary>
public interface IPermissionService
{
    /// <summary>
    /// Throws <see cref="SparkAccessDeniedException"/> if the current user
    /// does not have permission for the given action on the entity type.
    /// Apps without a registered access-control policy (neither
    /// <c>spark.AddAuthorization()</c> nor <c>spark.AllowAnonymousAccess()</c>)
    /// inherit a deny-all default and every call throws.
    /// </summary>
    /// <param name="action">The action (e.g., "Read", "Query", "New", "Edit", "Delete", "Execute")</param>
    /// <param name="target">The target (e.g., entity CLR type name or query name)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task EnsureAuthorizedAsync(string action, string target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the current user has permission for the given action on the entity type.
    /// Returns false when authorization is not configured (deny-all default).
    /// </summary>
    /// <param name="action">The action (e.g., "Read", "Query", "New", "Edit", "Delete", "Execute")</param>
    /// <param name="target">The target (e.g., entity CLR type name or query name)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if access is allowed, false otherwise</returns>
    Task<bool> IsAllowedAsync(string action, string target, CancellationToken cancellationToken = default);
}
