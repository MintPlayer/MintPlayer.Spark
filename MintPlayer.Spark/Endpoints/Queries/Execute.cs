using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Queries;

[Register(ServiceLifetime.Scoped)]
public sealed partial class ExecuteQuery
{
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IQueryExecutor queryExecutor;

    public async Task HandleAsync(HttpContext httpContext, Guid id)
    {
        var query = queryLoader.GetQuery(id);

        if (query is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Query with ID {id} not found" });
            return;
        }

        // Read optional sort overrides from query string
        var sortBy = httpContext.Request.Query["sortBy"].FirstOrDefault();
        var sortDirection = httpContext.Request.Query["sortDirection"].FirstOrDefault();

        // Clone query with sort overrides if provided
        var effectiveQuery = new SparkQuery
        {
            Id = query.Id,
            Name = query.Name,
            ContextProperty = query.ContextProperty,
            SortBy = !string.IsNullOrEmpty(sortBy) ? sortBy : query.SortBy,
            SortDirection = !string.IsNullOrEmpty(sortDirection) ? sortDirection : query.SortDirection,
            IndexName = query.IndexName,
            UseProjection = query.UseProjection,
        };

        var results = await queryExecutor.ExecuteQueryAsync(effectiveQuery);
        await httpContext.Response.WriteAsJsonAsync(results);
    }
}
