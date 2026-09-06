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

        // The id is a catch-all segment, so it also matches the bare "/{objectTypeId}" path — with
        // nothing in it. That used to be someone else's route: a list endpoint sat on the bare path
        // and won the match. It was deleted (a second, uncapped list pipeline), and this route
        // inherited the shape, where the ! on a null RouteValue was a NullReferenceException and a
        // 500 rather than a refusal.
        //
        // Answer exactly as for an id that names nothing, which is what an empty id is.
        var id = httpContext.Request.RouteValues["id"]?.ToString();
        if (string.IsNullOrEmpty(id))
        {
            return SparkDenial.RefuseJson(httpContext);
        }

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
