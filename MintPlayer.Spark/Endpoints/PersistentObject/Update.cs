using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Helpers;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

[Register(ServiceLifetime.Scoped, "AddSparkServices")]
public sealed partial class UpdatePersistentObject
{
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ICollectionHelper collectionHelper;
    [Inject] private readonly IValidationService validationService;

    public async Task HandleAsync(HttpContext httpContext, Guid objectTypeId, string id)
    {
        var decodedId = Uri.UnescapeDataString(id);
        var existingObj = await databaseAccess.GetPersistentObjectAsync(objectTypeId, decodedId);

        if (existingObj is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Object with ID {decodedId} not found" });
            return;
        }

        var obj = await httpContext.Request.ReadFromJsonAsync<Abstractions.PersistentObject>()
            ?? throw new InvalidOperationException("PersistentObject could not be deserialized from the request body.");

        // Ensure the ID and ObjectTypeId match the URL parameters
        var entityType = modelLoader.GetEntityType(objectTypeId)
            ?? throw new InvalidOperationException($"EntityType with ID {objectTypeId} not found");
        var collectionName = collectionHelper.GetCollectionName(entityType.ClrType);
        obj.Id = $"{collectionName}/{decodedId}";
        obj.ObjectTypeId = objectTypeId;

        // Validate the object
        var validationResult = validationService.Validate(obj);
        if (!validationResult.IsValid)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { errors = validationResult.Errors });
            return;
        }

        var result = await databaseAccess.SavePersistentObjectAsync(obj);
        await httpContext.Response.WriteAsJsonAsync(result);
    }
}
