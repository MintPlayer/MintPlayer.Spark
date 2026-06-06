using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.EntityTypes;

internal sealed partial class GetEntityType : IGetEndpoint, IMemberOf<EntityTypesGroup>
{
    public static string Path => "/{id}";

    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPermissionService permissionService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var id = httpContext.Request.RouteValues["id"]!.ToString()!;
        var entityType = modelLoader.ResolveEntityType(id);

        if (entityType is null)
            return Results.Json(new { error = $"Entity type '{id}' not found" }, statusCode: 404);

        // 404 rather than 403 — so existence isn't leaked to callers without Query rights.
        if (!await permissionService.IsAllowedAsync("Query", entityType.Name, httpContext.RequestAborted))
            return Results.Json(new { error = $"Entity type '{id}' not found" }, statusCode: 404);

        return Results.Json(entityType);
    }
}
