using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

[Register(ServiceLifetime.Scoped, "AddSparkServices")]
public sealed partial class UpdatePersistentObject
{
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;

    public async Task HandleAsync(HttpContext httpContext, Guid objectTypeId, string id)
    {
        var existingObj = await databaseAccess.GetPersistentObjectAsync(objectTypeId, id);

        if (existingObj is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Object with ID {id} not found" });
            return;
        }

        var obj = await httpContext.Request.ReadFromJsonAsync<Abstractions.PersistentObject>()
            ?? throw new InvalidOperationException("PersistentObject could not be deserialized from the request body.");

        // Ensure the ID and ObjectTypeId match the URL parameters
        var entityType = modelLoader.GetEntityType(objectTypeId)
            ?? throw new InvalidOperationException($"EntityType with ID {objectTypeId} not found");
        var collectionName = Helpers.CollectionHelper.GetCollectionName(entityType.ClrType);
        obj.Id = $"{collectionName}/{id}";
        obj.ObjectTypeId = objectTypeId;

        var result = await databaseAccess.SavePersistentObjectAsync(obj);
        await httpContext.Response.WriteAsJsonAsync(result);
    }
}
