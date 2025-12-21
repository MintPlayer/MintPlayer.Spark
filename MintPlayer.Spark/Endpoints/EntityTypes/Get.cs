using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.EntityTypes;

[Register(ServiceLifetime.Scoped, "AddSparkServices")]
public sealed partial class GetEntityType
{
    [Inject] private readonly IModelLoader modelLoader;

    public async Task HandleAsync(HttpContext httpContext, Guid id)
    {
        var entityType = modelLoader.GetEntityType(id);

        if (entityType is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Entity type with ID {id} not found" });
            return;
        }

        await httpContext.Response.WriteAsJsonAsync(entityType);
    }
}
