using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Queries;

[Register(ServiceLifetime.Scoped)]
public sealed partial class ListQueries : IEndpoint
{
    [Inject] private readonly IQueryLoader queryLoader;

    public static void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/", async (HttpContext context, ListQueries action) =>
            await action.HandleAsync(context));
    }

    public async Task HandleAsync(HttpContext httpContext)
    {
        var queries = queryLoader.GetQueries();
        await httpContext.Response.WriteAsJsonAsync(queries);
    }
}
