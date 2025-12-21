using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

[Register(ServiceLifetime.Scoped, "AddSparkServices")]
public sealed partial class GetPersistentObject
{
    [Inject] private readonly IDatabaseAccess databaseAccess;

    public async Task HandleAsync(HttpContext httpContext, Guid objectTypeId, string id)
    {
        var obj = await databaseAccess.GetPersistentObjectAsync(objectTypeId, id);

        if (obj is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Object with ID {id} not found" });
            return;
        }

        await httpContext.Response.WriteAsJsonAsync(obj);
    }
}
