using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Helpers;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

[Register(ServiceLifetime.Scoped, "AddSparkServices")]
public sealed partial class UpdatePersistentObject
{
    [Inject] private readonly IDatabaseAccess databaseAccess;

    public async Task HandleAsync(HttpContext httpContext, string type, string id)
    {
        var existingObj = await databaseAccess.GetPersistentObjectAsync(type, id);

        if (existingObj is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"{type} with ID {id} not found" });
            return;
        }

        var obj = await httpContext.Request.ReadFromJsonAsync<Abstractions.PersistentObject>()
            ?? throw new InvalidOperationException(type + " could not be deserialized from the request body.");

        // Ensure the ID and ClrType match the URL parameters
        var collectionName = CollectionHelper.GetCollectionName(type);
        obj.Id = $"{collectionName}/{id}";
        obj.ClrType = type;

        var result = await databaseAccess.SavePersistentObjectAsync(obj);
        await httpContext.Response.WriteAsJsonAsync(result);
    }
}
