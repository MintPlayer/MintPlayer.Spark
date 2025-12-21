using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

[Register(ServiceLifetime.Scoped, "AddSparkServices")]
public sealed partial class GetPersistentObject
{
    [Inject] private readonly IDatabaseAccess databaseAccess;

    public async Task HandleAsync(HttpContext httpContext, string type, string id)
    {
        var documentId = $"PersistentObjects/{id}";
        var obj = await databaseAccess.GetDocumentAsync<Abstractions.PersistentObject>(documentId);

        if (obj is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"{type} with ID {id} not found" });
            return;
        }

        await httpContext.Response.WriteAsJsonAsync(obj);
    }
}
