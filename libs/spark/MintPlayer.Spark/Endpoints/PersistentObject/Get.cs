using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

internal sealed partial class GetPersistentObject : IGetEndpoint, IMemberOf<PersistentObjectGroup>
{
    public static string Path => "/{objectTypeId}/{**id}";

    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var objectTypeId = httpContext.Request.RouteValues["objectTypeId"]!.ToString()!;
        var id = httpContext.Request.RouteValues["id"]!.ToString()!;

        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            return SparkDenial.RefuseJson(httpContext);
        }

        try
        {
            var decodedId = Uri.UnescapeDataString(id);
            var obj = await databaseAccess.GetPersistentObjectAsync(entityType.Id, decodedId);

            if (obj is null)
            {
                return SparkDenial.RefuseJson(httpContext);
            }

            return Results.Json(obj);
        }
        catch (SparkAccessDeniedException)
        {
            return SparkDenial.RefuseJson(httpContext);
        }
    }
}
