using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Queries;

internal sealed partial class ListQueries : IGetEndpoint, IMemberOf<QueriesGroup>
{
    public static string Path => "/";

    [Inject] private readonly IQueryLoader queryLoader;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var queries = queryLoader.GetQueries();
        return Results.Json(queries);
    }
}
