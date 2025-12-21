using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

[Register(ServiceLifetime.Scoped, "AddSparkServices")]
public sealed partial class CreatePersistentObject
{
    [Inject] private readonly IDatabaseAccess databaseAccess;

    public async Task HandleAsync(HttpContext httpContext, string type)
    {
        var obj = await httpContext.Request.ReadFromJsonAsync<Abstractions.PersistentObject>()
            ?? throw new InvalidOperationException(type + " could not be deserialized from the request body.");

        // Ensure the ClrType matches the URL type parameter
        obj.ClrType = type;

        // Generate a new ID if not provided
        if (string.IsNullOrEmpty(obj.Id))
        {
            obj.Id = $"PersistentObjects/{Guid.NewGuid()}";
        }

        var result = await databaseAccess.SaveDocumentAsync(obj);

        httpContext.Response.StatusCode = StatusCodes.Status201Created;
        await httpContext.Response.WriteAsJsonAsync(result);
    }
}
