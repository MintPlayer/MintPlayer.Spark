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

    public async Task HandleAsync(HttpContext httpContext, string id)
    {
        var query = queryLoader.ResolveQuery(id);

        if (query is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Query '{id}' not found" });
            return;
        }

        // Authorization check (only when IAccessControl is registered)
        var accessControl = httpContext.RequestServices.GetService<IAccessControl>();
        if (accessControl is not null)
        {
            if (!await accessControl.IsAllowedAsync($"Execute/{query.Name}"))
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Access denied" });
                return;
            }
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
            Alias = query.Alias,
            SortBy = !string.IsNullOrEmpty(sortBy) ? sortBy : query.SortBy,
            SortDirection = !string.IsNullOrEmpty(sortDirection) ? sortDirection : query.SortDirection,
            IndexName = query.IndexName,
            UseProjection = query.UseProjection,
        };

        var results = await queryExecutor.ExecuteQueryAsync(effectiveQuery);
        await httpContext.Response.WriteAsJsonAsync(results);
    }
}
