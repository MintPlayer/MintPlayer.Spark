using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

[Register(ServiceLifetime.Scoped, "AddSparkServices")]
public sealed partial class ListPersistentObjects
{
    [Inject] private readonly IDatabaseAccess databaseAccess;

    public async Task HandleAsync(HttpContext httpContext, string type)
    {
        var objects = await databaseAccess.GetDocumentsByTypeAsync<Abstractions.PersistentObject>(type);
        await httpContext.Response.WriteAsJsonAsync(objects);
    }
}
