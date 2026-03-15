using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.EntityTypes;

[Register(ServiceLifetime.Scoped)]
public sealed partial class ListEntityTypes : IEndpoint
{
    [Inject] private readonly IModelLoader modelLoader;

    public static void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/", async (HttpContext context, ListEntityTypes action) =>
            await action.HandleAsync(context));
    }

    public async Task HandleAsync(HttpContext httpContext)
    {
        var entityTypes = modelLoader.GetEntityTypes();
        await httpContext.Response.WriteAsJsonAsync(entityTypes);
    }
}
