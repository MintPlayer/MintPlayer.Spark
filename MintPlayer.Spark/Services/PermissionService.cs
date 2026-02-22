using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

[Register(typeof(IPermissionService), ServiceLifetime.Scoped)]
internal partial class PermissionService : IPermissionService
{
    [Inject] private readonly IAccessControl? accessControl;

    public async Task EnsureAuthorizedAsync(string action, string target, CancellationToken cancellationToken = default)
    {
        if (accessControl is null)
            return;

        var resource = $"{action}/{target}";
        if (!await accessControl.IsAllowedAsync(resource, cancellationToken))
            throw new SparkAccessDeniedException(resource);
    }

    public async Task<bool> IsAllowedAsync(string action, string target, CancellationToken cancellationToken = default)
    {
        if (accessControl is null)
            return true;

        var resource = $"{action}/{target}";
        return await accessControl.IsAllowedAsync(resource, cancellationToken);
    }
}
