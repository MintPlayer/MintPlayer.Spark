using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

[Register(ServiceLifetime.Scoped)]
public sealed partial class DeletePersistentObject
{
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;

    public async Task HandleAsync(HttpContext httpContext, string objectTypeId, string id)
    {
        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Entity type '{objectTypeId}' not found" });
            return;
        }

        var decodedId = Uri.UnescapeDataString(id);
        var obj = await databaseAccess.GetPersistentObjectAsync(entityType.Id, decodedId);

        if (obj is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Object with ID {decodedId} not found" });
            return;
        }

        await databaseAccess.DeletePersistentObjectAsync(entityType.Id, decodedId);
        httpContext.Response.StatusCode = StatusCodes.Status204NoContent;
    }
}
