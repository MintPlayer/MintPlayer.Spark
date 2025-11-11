using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

public sealed partial class CreatePersistentObject
{
    [Inject] private readonly IDatabaseAccess databaseAccess;

    public async Task HandleAsync(HttpContext httpContext, string type)
    {
        var obj = await httpContext.Request.ReadFromJsonAsync<Abstractions.PersistentObject>() ?? throw new InvalidOperationException(type + " could not be deserialized from the request body.");
        var result = await databaseAccess.SaveDocumentAsync(obj);
        await httpContext.Response.WriteAsJsonAsync(result);
    }
}
