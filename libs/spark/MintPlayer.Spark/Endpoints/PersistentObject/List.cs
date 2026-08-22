using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

internal sealed partial class ListPersistentObjects : IGetEndpoint, IMemberOf<PersistentObjectGroup>
{
    public static string Path => "/{objectTypeId}";

    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var objectTypeId = httpContext.Request.RouteValues["objectTypeId"]!.ToString()!;

        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            return SparkDenial.RefuseJson(httpContext);
        }

        try
        {
            var objects = await databaseAccess.GetPersistentObjectsAsync(entityType.Id);
            return Results.Json(objects);
        }
        catch (SparkAccessDeniedException)
        {
            return SparkDenial.RefuseJson(httpContext);
        }
    }
}
