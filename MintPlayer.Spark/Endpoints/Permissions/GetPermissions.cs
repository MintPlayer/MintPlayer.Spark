using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Permissions;

internal sealed partial class GetPermissions : IGetEndpoint, IMemberOf<SparkGroup>
{
    public static string Path => "/permissions/{entityTypeId}";

    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IModelLoader modelLoader;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var entityTypeId = (string)httpContext.Request.RouteValues["entityTypeId"]!;

        var entityType = modelLoader.ResolveEntityType(entityTypeId);
        if (entityType is null)
        {
            return Results.Json(new { error = $"Entity type '{entityTypeId}' not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        var target = entityType.Name;
        var canRead = await permissionService.IsAllowedAsync("Read", target);
        var canCreate = await permissionService.IsAllowedAsync("New", target);
        var canEdit = await permissionService.IsAllowedAsync("Edit", target);
        var canDelete = await permissionService.IsAllowedAsync("Delete", target);

        return Results.Json(new { canRead, canCreate, canEdit, canDelete });
    }
}
