using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Permissions;

[Register(ServiceLifetime.Scoped)]
public sealed partial class GetPermissions
{
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IModelLoader modelLoader;

    public async Task HandleAsync(HttpContext httpContext, string entityTypeId)
    {
        var entityType = modelLoader.ResolveEntityType(entityTypeId);
        if (entityType is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Entity type '{entityTypeId}' not found" });
            return;
        }

        var target = entityType.Name;
        var canCreate = await permissionService.IsAllowedAsync("New", target);
        var canEdit = await permissionService.IsAllowedAsync("Edit", target);
        var canDelete = await permissionService.IsAllowedAsync("Delete", target);

        await httpContext.Response.WriteAsJsonAsync(new { canCreate, canEdit, canDelete });
    }
}
