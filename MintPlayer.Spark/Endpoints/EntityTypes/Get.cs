using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.EntityTypes;

internal sealed partial class GetEntityType : IGetEndpoint, IMemberOf<EntityTypesGroup>
{
    public static string Path => "/{id}";

    [Inject] private readonly IModelLoader modelLoader;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var id = httpContext.Request.RouteValues["id"]!.ToString()!;
        var entityType = modelLoader.ResolveEntityType(id);

        if (entityType is null)
        {
            return Results.Json(new { error = $"Entity type '{id}' not found" }, statusCode: 404);
        }

        return Results.Json(entityType);
    }
}
