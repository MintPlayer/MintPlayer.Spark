using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

[Register(ServiceLifetime.Scoped)]
public sealed partial class ListPersistentObjects
{
    [Inject] private readonly IDatabaseAccess databaseAccess;

    public async Task HandleAsync(HttpContext httpContext, Guid objectTypeId)
    {
        var objects = await databaseAccess.GetPersistentObjectsAsync(objectTypeId);
        await httpContext.Response.WriteAsJsonAsync(objects);
    }
}
