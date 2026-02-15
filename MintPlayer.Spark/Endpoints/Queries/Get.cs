using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Queries;

[Register(ServiceLifetime.Scoped)]
public sealed partial class GetQuery
{
    [Inject] private readonly IQueryLoader queryLoader;

    public async Task HandleAsync(HttpContext httpContext, string id)
    {
        var query = queryLoader.ResolveQuery(id);

        if (query is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Query '{id}' not found" });
            return;
        }

        await httpContext.Response.WriteAsJsonAsync(query);
    }
}
