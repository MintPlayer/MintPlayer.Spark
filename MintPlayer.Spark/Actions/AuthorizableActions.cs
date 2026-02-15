using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Actions;

/// <summary>
/// Optional base class for Actions that need access to authorization checks.
/// Use this instead of <see cref="DefaultPersistentObjectActions{T}"/> if you need
/// to check permissions in custom action methods (e.g., Approve, Export, etc.).
/// </summary>
/// <typeparam name="T">The entity type</typeparam>
/// <example>
/// <code>
/// public partial class PersonActions : AuthorizableActions&lt;Person&gt;
/// {
///     public async Task&lt;bool&gt; ApproveAsync(IAsyncDocumentSession session, string id)
///     {
///         await EnsureAllowedAsync("Approve");
///
///         var person = await session.LoadAsync&lt;Person&gt;(id);
///         person.IsApproved = true;
///         await session.SaveChangesAsync();
///         return true;
///     }
/// }
/// </code>
/// </example>
public abstract partial class AuthorizableActions<T> : DefaultPersistentObjectActions<T> where T : class
{
    [Inject] private readonly IServiceProvider? serviceProvider;

    /// <summary>
    /// Gets the entity type name used in resource strings.
    /// Override this to customize the entity type identifier.
    /// </summary>
    protected virtual string EntityTypeName => typeof(T).FullName ?? typeof(T).Name;

    /// <summary>
    /// Checks if the current user has permission to perform an action.
    /// Returns true when no IAccessControl is registered (authorization not enabled).
    /// </summary>
    /// <param name="action">The action to check (e.g., "Read", "Edit", "Approve")</param>
    /// <param name="propertyName">Optional property name for field-level security</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if access is allowed, false otherwise</returns>
    protected virtual async Task<bool> IsAllowedAsync(
        string action,
        string? propertyName = null,
        CancellationToken cancellationToken = default)
    {
        if (serviceProvider == null)
            return true;

        var accessControl = serviceProvider.GetService(typeof(IAccessControl)) as IAccessControl;
        if (accessControl is null)
            return true;

        var resource = string.IsNullOrEmpty(propertyName)
            ? $"{action}/{EntityTypeName}"
            : $"{action}/{EntityTypeName}/{propertyName}";

        return await accessControl.IsAllowedAsync(resource, cancellationToken);
    }

    /// <summary>
    /// Throws <see cref="UnauthorizedAccessException"/> if the current user doesn't have permission.
    /// Use this when you want to fail fast on unauthorized access.
    /// </summary>
    /// <param name="action">The action to check (e.g., "Read", "Edit", "Approve")</param>
    /// <param name="propertyName">Optional property name for field-level security</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <exception cref="UnauthorizedAccessException">Thrown when access is denied</exception>
    protected async Task EnsureAllowedAsync(
        string action,
        string? propertyName = null,
        CancellationToken cancellationToken = default)
    {
        if (!await IsAllowedAsync(action, propertyName, cancellationToken))
        {
            var resource = string.IsNullOrEmpty(propertyName)
                ? $"{action}/{EntityTypeName}"
                : $"{action}/{EntityTypeName}/{propertyName}";

            throw new UnauthorizedAccessException($"Access denied for resource: {resource}");
        }
    }
}
