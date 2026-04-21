using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Queries;

internal sealed partial class ListQueries : IGetEndpoint, IMemberOf<QueriesGroup>
{
    public static string Path => "/";

    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IPermissionService permissionService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var queries = queryLoader.GetQueries();
        var visible = new List<Abstractions.SparkQuery>(queries.Count());
        foreach (var query in queries)
        {
            // Queries without a target entity type cannot be permission-checked and are
            // conservatively hidden. When EntityType is set, the caller needs at least
            // Query rights on that entity to see the query in the list.
            if (query.EntityType is null)
                continue;
            if (await permissionService.IsAllowedAsync("Query", query.EntityType, httpContext.RequestAborted))
                visible.Add(query);
        }
        return Results.Json(visible);
    }
}
