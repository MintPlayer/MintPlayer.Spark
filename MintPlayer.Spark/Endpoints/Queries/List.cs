using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Queries;

[Register(ServiceLifetime.Scoped, "AddSparkServices")]
public sealed partial class ListQueries
{
    [Inject] private readonly IQueryLoader queryLoader;

    public async Task HandleAsync(HttpContext httpContext)
    {
        var queries = queryLoader.GetQueries();
        await httpContext.Response.WriteAsJsonAsync(queries);
    }
}
