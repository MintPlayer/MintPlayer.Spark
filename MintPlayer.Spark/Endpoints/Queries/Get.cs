using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Queries;

[Register(ServiceLifetime.Scoped)]
public sealed partial class GetQuery : IEndpoint
{
    [Inject] private readonly IQueryLoader queryLoader;

    public static void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/{id}", async (HttpContext context, string id, GetQuery action) =>
            await action.HandleAsync(context, id));
    }

    public async Task HandleAsync(HttpContext httpContext, string id)
    {
        var query = queryLoader.ResolveQuery(id);

        if (query is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Query '{id}' not found" });
            return;
        }

        await httpContext.Response.WriteAsJsonAsync(query);
    }
}
