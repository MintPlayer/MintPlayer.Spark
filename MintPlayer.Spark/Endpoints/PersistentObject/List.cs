using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

[Register(ServiceLifetime.Scoped, "AddSparkServices")]
public sealed partial class ListPersistentObjects
{
    public async Task HandleAsync(HttpContext httpContext, string type)
    {
        await httpContext.Response.WriteAsync($"Get all {type}s!");
    }
}
