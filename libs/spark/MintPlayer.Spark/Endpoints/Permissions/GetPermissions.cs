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

        // canQuery is reported alongside the rest because Query and Read are independently
        // grantable: 'Query/Person' alone lists rows while refusing a by-id load, and 'Read/Person'
        // alone does the reverse. The combined 'QueryRead' bundles them invisibly, so the one right
        // it adds beyond a reader's expectation was precisely the one introspection never mentioned
        // — a client could not tell "no grid" from "no permissions endpoint" (#298).
        var canQuery = await permissionService.IsAllowedAsync("Query", target);
        var canRead = await permissionService.IsAllowedAsync("Read", target);
        var canCreate = await permissionService.IsAllowedAsync("New", target);
        var canEdit = await permissionService.IsAllowedAsync("Edit", target);
        var canDelete = await permissionService.IsAllowedAsync("Delete", target);

        return Results.Json(new { canQuery, canRead, canCreate, canEdit, canDelete });
    }
}
