using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Queries;

internal sealed partial class GetQuery : IGetEndpoint, IMemberOf<QueriesGroup>
{
    public static string Path => "/{id}";

    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IPermissionService permissionService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var id = httpContext.Request.RouteValues["id"]!.ToString()!;
        var query = queryLoader.ResolveQuery(id);

        if (query is null)
            return Results.Json(new { error = $"Query '{id}' not found" }, statusCode: 404);

        // Return 404 (not 403) when the caller isn't authorized — so existence isn't leaked.
        if (query.EntityType is null ||
            !await permissionService.IsAllowedAsync("Query", query.EntityType, httpContext.RequestAborted))
        {
            return Results.Json(new { error = $"Query '{id}' not found" }, statusCode: 404);
        }

        return Results.Json(query);
    }
}
