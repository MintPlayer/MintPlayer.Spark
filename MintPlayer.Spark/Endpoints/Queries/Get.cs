using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Queries;

internal sealed partial class GetQuery : IGetEndpoint, IMemberOf<QueriesGroup>
{
    public static string Path => "/{id}";

    [Inject] private readonly IQueryLoader queryLoader;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var id = httpContext.Request.RouteValues["id"]!.ToString()!;
        var query = queryLoader.ResolveQuery(id);

        if (query is null)
        {
            return Results.Json(new { error = $"Query '{id}' not found" }, statusCode: 404);
        }

        return Results.Json(query);
    }
}
