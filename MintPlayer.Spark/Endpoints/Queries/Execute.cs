using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Queries;

internal sealed partial class ExecuteQuery : IGetEndpoint, IMemberOf<QueriesGroup>
{
    public static string Path => "/{id}/execute";

    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IQueryExecutor queryExecutor;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var id = httpContext.Request.RouteValues["id"]!.ToString()!;
        var query = queryLoader.ResolveQuery(id);

        if (query is null)
        {
            return Results.Json(new { error = $"Query '{id}' not found" }, statusCode: 404);
        }

        try
        {
            // Read optional sort overrides from query string
            var sortColumnsParam = httpContext.Request.Query["sortColumns"].FirstOrDefault();
            SortColumn[]? sortOverrides = null;
            if (!string.IsNullOrEmpty(sortColumnsParam))
            {
                sortOverrides = sortColumnsParam.Split(',')
                    .Select(part =>
                    {
                        var segments = part.Split(':');
                        return new SortColumn
                        {
                            Property = segments[0],
                            Direction = segments.Length > 1 ? segments[1] : "asc"
                        };
                    })
                    .ToArray();
            }

            // Read pagination parameters
            var skipParam = httpContext.Request.Query["skip"].FirstOrDefault();
            var takeParam = httpContext.Request.Query["take"].FirstOrDefault();
            var search = httpContext.Request.Query["search"].FirstOrDefault();
            int skip = int.TryParse(skipParam, out var s) ? s : 0;
            int take = int.TryParse(takeParam, out var t) ? t : 50;

            // Read optional parent context for custom queries
            Abstractions.PersistentObject? parent = null;
            var parentId = httpContext.Request.Query["parentId"].FirstOrDefault();
            var parentType = httpContext.Request.Query["parentType"].FirstOrDefault();
            if (!string.IsNullOrEmpty(parentId) && !string.IsNullOrEmpty(parentType))
            {
                var parentEntityType = modelLoader.ResolveEntityType(parentType);
                if (parentEntityType != null)
                {
                    parent = await databaseAccess.GetPersistentObjectAsync(parentEntityType.Id, parentId);
                }
            }

            // Clone query with sort overrides if provided
            var effectiveQuery = new SparkQuery
            {
                Id = query.Id,
                Name = query.Name,
                Source = query.Source,
                Alias = query.Alias,
                SortColumns = sortOverrides ?? query.SortColumns,
                RenderMode = query.RenderMode,
                IndexName = query.IndexName,
                UseProjection = query.UseProjection,
                EntityType = query.EntityType,
                IsStreamingQuery = query.IsStreamingQuery,
            };

            var results = await queryExecutor.ExecuteQueryAsync(effectiveQuery, parent, skip, take, search);
            return Results.Json(results);
        }
        catch (SparkAccessDeniedException)
        {
            if (httpContext.User.Identity?.IsAuthenticated != true)
            {
                return Results.Json(new { error = "Authentication required" }, statusCode: 401);
            }
            else
            {
                return Results.Json(new { error = "Access denied" }, statusCode: 403);
            }
        }
    }
}
