using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

[Register(ServiceLifetime.Scoped, "AddSparkServices")]
public sealed partial class GetPersistentObject
{
    public async Task HandleAsync(HttpContext httpContext, string type, Guid id)
    {
        await httpContext.Response.WriteAsync($"Get {type} with ID {id}!");
    }
}
