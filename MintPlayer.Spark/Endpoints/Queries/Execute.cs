using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Queries;

[Register(ServiceLifetime.Scoped)]
public sealed partial class ExecuteQuery
{
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IQueryExecutor queryExecutor;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;

    public async Task HandleAsync(HttpContext httpContext, string id)
    {
        var query = queryLoader.ResolveQuery(id);

        if (query is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Query '{id}' not found" });
            return;
        }

        try
        {
            // Read optional sort overrides from query string
            var sortBy = httpContext.Request.Query["sortBy"].FirstOrDefault();
            var sortDirection = httpContext.Request.Query["sortDirection"].FirstOrDefault();

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
                SortBy = !string.IsNullOrEmpty(sortBy) ? sortBy : query.SortBy,
                SortDirection = !string.IsNullOrEmpty(sortDirection) ? sortDirection : query.SortDirection,
                IndexName = query.IndexName,
                UseProjection = query.UseProjection,
                EntityType = query.EntityType,
                IsStreamingQuery = query.IsStreamingQuery,
            };

            var results = await queryExecutor.ExecuteQueryAsync(effectiveQuery, parent);
            await httpContext.Response.WriteAsJsonAsync(results);
        }
        catch (SparkAccessDeniedException)
        {
            if (httpContext.User.Identity?.IsAuthenticated != true)
            {
                httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Authentication required" });
            }
            else
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Access denied" });
            }
        }
    }
}
