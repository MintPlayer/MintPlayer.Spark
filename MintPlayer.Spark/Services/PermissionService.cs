using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

[Register(typeof(IPermissionService), ServiceLifetime.Scoped)]
internal partial class PermissionService : IPermissionService
{
    // accessControl is always non-null: AddSpark registers a deny-all default;
    // spark.AddAuthorization() or spark.AllowAnonymousAccess() replaces it.
    // Per R2-H1, removing the previous "null => allow" branch was the fix —
    // forgotten-AddAuthorization no longer silently opens every endpoint.
    [Inject] private readonly IAccessControl accessControl;

    public async Task EnsureAuthorizedAsync(string action, string target, CancellationToken cancellationToken = default)
    {
        var resource = $"{action}/{target}";
        if (!await accessControl.IsAllowedAsync(resource, cancellationToken))
            throw new SparkAccessDeniedException(resource);
    }

    public async Task<bool> IsAllowedAsync(string action, string target, CancellationToken cancellationToken = default)
    {
        var resource = $"{action}/{target}";
        return await accessControl.IsAllowedAsync(resource, cancellationToken);
    }
}
