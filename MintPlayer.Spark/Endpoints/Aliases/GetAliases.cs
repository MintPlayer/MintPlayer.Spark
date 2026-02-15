using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Aliases;

[Register(ServiceLifetime.Scoped)]
public sealed partial class GetAliases
{
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IQueryLoader queryLoader;

    public async Task HandleAsync(HttpContext httpContext)
    {
        var entityTypeAliases = modelLoader.GetEntityTypes()
            .Where(e => e.Alias != null)
            .ToDictionary(e => e.Id.ToString(), e => e.Alias!);

        var queryAliases = queryLoader.GetQueries()
            .Where(q => q.Alias != null)
            .ToDictionary(q => q.Id.ToString(), q => q.Alias!);

        await httpContext.Response.WriteAsJsonAsync(new
        {
            entityTypes = entityTypeAliases,
            queries = queryAliases
        });
    }
}
