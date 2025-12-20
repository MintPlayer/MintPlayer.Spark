using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.EntityTypes;

[Register(ServiceLifetime.Scoped, "AddSparkServices")]
public sealed partial class ListEntityTypes
{
    [Inject] private readonly IModelLoader modelLoader;

    public async Task HandleAsync(HttpContext httpContext)
    {
        var entityTypes = modelLoader.GetEntityTypes();
        await httpContext.Response.WriteAsJsonAsync(entityTypes);
    }
}
