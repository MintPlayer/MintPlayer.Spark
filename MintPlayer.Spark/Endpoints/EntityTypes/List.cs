using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.EntityTypes;

internal sealed partial class ListEntityTypes : IGetEndpoint, IMemberOf<EntityTypesGroup>
{
    public static string Path => "/";

    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPermissionService permissionService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var entityTypes = modelLoader.GetEntityTypes();
        var visible = new List<EntityTypeDefinition>(entityTypes.Count());
        foreach (var entityType in entityTypes)
        {
            if (await permissionService.IsAllowedAsync("Query", entityType.Name, httpContext.RequestAborted))
                visible.Add(entityType);
        }
        return Results.Json(visible);
    }
}
