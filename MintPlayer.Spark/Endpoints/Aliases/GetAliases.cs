using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Aliases;

internal sealed partial class GetAliases : IGetEndpoint, IMemberOf<SparkGroup>
{
    public static string Path => "/aliases";

    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IPermissionService permissionService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var entityTypeAliases = new Dictionary<string, string>();
        foreach (var entityType in modelLoader.GetEntityTypes())
        {
            if (entityType.Alias is null) continue;
            if (await permissionService.IsAllowedAsync("Query", entityType.Name, httpContext.RequestAborted))
                entityTypeAliases[entityType.Id.ToString()] = entityType.Alias;
        }

        var queryAliases = new Dictionary<string, string>();
        foreach (var query in queryLoader.GetQueries())
        {
            if (query.Alias is null || query.EntityType is null) continue;
            if (await permissionService.IsAllowedAsync("Query", query.EntityType, httpContext.RequestAborted))
                queryAliases[query.Id.ToString()] = query.Alias;
        }

        return Results.Json(new
        {
            entityTypes = entityTypeAliases,
            queries = queryAliases
        });
    }
}
