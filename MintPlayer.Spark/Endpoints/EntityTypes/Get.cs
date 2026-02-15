using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.EntityTypes;

[Register(ServiceLifetime.Scoped)]
public sealed partial class GetEntityType
{
    [Inject] private readonly IModelLoader modelLoader;

    public async Task HandleAsync(HttpContext httpContext, string id)
    {
        var entityType = modelLoader.ResolveEntityType(id);

        if (entityType is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Entity type '{id}' not found" });
            return;
        }

        await httpContext.Response.WriteAsJsonAsync(entityType);
    }
}
