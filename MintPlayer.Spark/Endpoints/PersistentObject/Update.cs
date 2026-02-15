using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

[Register(ServiceLifetime.Scoped)]
public sealed partial class UpdatePersistentObject
{
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IValidationService validationService;
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
        var existingObj = await databaseAccess.GetPersistentObjectAsync(entityType.Id, decodedId);

        if (existingObj is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Object with ID {decodedId} not found" });
            return;
        }

        var obj = await httpContext.Request.ReadFromJsonAsync<Abstractions.PersistentObject>()
            ?? throw new InvalidOperationException("PersistentObject could not be deserialized from the request body.");

        // Ensure the ID and ObjectTypeId match the URL parameters
        obj.Id = existingObj.Id;
        obj.ObjectTypeId = entityType.Id;

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
