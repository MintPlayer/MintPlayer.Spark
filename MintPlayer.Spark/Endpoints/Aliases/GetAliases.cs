using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Aliases;

internal sealed partial class GetAliases : IGetEndpoint, IMemberOf<SparkGroup>
{
    public static string Path => "/aliases";

    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IQueryLoader queryLoader;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var entityTypeAliases = modelLoader.GetEntityTypes()
            .Where(e => e.Alias != null)
            .ToDictionary(e => e.Id.ToString(), e => e.Alias!);

        var queryAliases = queryLoader.GetQueries()
            .Where(q => q.Alias != null)
            .ToDictionary(q => q.Id.ToString(), q => q.Alias!);

        return Results.Json(new
        {
            entityTypes = entityTypeAliases,
            queries = queryAliases
        });
    }
}
