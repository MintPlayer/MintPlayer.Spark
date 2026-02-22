using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

[Register(ServiceLifetime.Scoped)]
public sealed partial class ListPersistentObjects
{
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;

    public async Task HandleAsync(HttpContext httpContext, string objectTypeId)
    {
        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Entity type '{objectTypeId}' not found" });
            return;
        }

        try
        {
            var objects = await databaseAccess.GetPersistentObjectsAsync(entityType.Id);
            await httpContext.Response.WriteAsJsonAsync(objects);
        }
        catch (SparkAccessDeniedException)
        {
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Access denied" });
        }
    }
}
