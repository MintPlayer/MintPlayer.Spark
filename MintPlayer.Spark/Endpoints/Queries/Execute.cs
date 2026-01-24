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

        var results = await queryExecutor.ExecuteQueryAsync(query);
        await httpContext.Response.WriteAsJsonAsync(results);
    }
}
