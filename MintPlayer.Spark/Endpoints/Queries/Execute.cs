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

                // Allow-list sort columns against the query's declared attribute set. Without
                // this check, a caller could sort by any public property on the projection
                // type via reflection (including fields the developer didn't expose as an
                // attribute), leaking ordering as a side channel. The query's own declared
                // sort columns are always allowed, so a query can opt in to otherwise-private
                // sort keys by declaring them up-front.
                var allowedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (query.EntityType is not null)
                {
                    var entityType = modelLoader.ResolveEntityType(query.EntityType);
                    if (entityType is not null)
                    {
                        foreach (var attr in entityType.Attributes)
                            allowedProperties.Add(attr.Name);
                    }
                }
                if (query.SortColumns is not null)
                {
                    foreach (var declared in query.SortColumns)
                        allowedProperties.Add(declared.Property);
                }

                var invalid = sortOverrides
                    .Where(c => !allowedProperties.Contains(c.Property))
                    .Select(c => c.Property)
                    .ToArray();
                if (invalid.Length > 0)
                {
                    return Results.Json(
                        new { error = $"Unknown sort column(s): {string.Join(", ", invalid)}" },
                        statusCode: 400);
                }
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
                // Parent was asked for but we couldn't resolve or couldn't authorize it.
                // Return 404 rather than silently running the query unscoped — that would
                // leak data the caller shouldn't see (H-3).
                if (parent is null)
                    return Results.Json(new { error = "Parent not found" }, statusCode: 404);
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
