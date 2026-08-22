using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

[Register(typeof(IPermissionService), ServiceLifetime.Scoped)]
internal partial class PermissionService : IPermissionService
{
    // Always non-null: AddSpark registers the security.json evaluator unconditionally, and
    // authorization is no longer something an application can forget to switch on. The
    // "null => allow" branch this replaced was the original fail-open path.
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
