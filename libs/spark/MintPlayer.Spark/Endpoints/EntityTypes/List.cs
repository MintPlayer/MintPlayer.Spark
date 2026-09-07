using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.EntityTypes;

internal sealed partial class ListEntityTypes : IGetEndpoint, IMemberOf<EntityTypesGroup>
{
    public static string Path => "/";

    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly ILogger<ListEntityTypes> logger;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var entityTypes = modelLoader.GetEntityTypes();
        var visible = new List<EntityTypeDefinition>(entityTypes.Count());
        foreach (var entityType in entityTypes)
        {
            // The catalogue is list-scoped: Query gates it, deliberately NOT Read. A type the
            // caller may only Read (a virtual start page, a Read-without-Query grant) is absent
            // here and resolved individually via GET /spark/types/{id}, which Read unlocks —
            // single-object metadata belongs to the single-object right.
            if (!await permissionService.IsAllowedAsync("Query", entityType.Name, httpContext.RequestAborted))
                continue;

            // This is the load-bearing one for sub-query pruning: spark-po-detail reads the array
            // from here and never calls getEntityType(id).
            var pruned = await SubQueryPruner.PruneAsync(
                entityType, queryLoader, permissionService, logger, httpContext.RequestAborted);

            // PruneAsync already copied the definition — ModelLoader is a singleton and its
            // instances are shared — so this per-caller answer can be written without leaking into
            // the next request's view of the model.
            pruned.CanRead = await permissionService.IsAllowedAsync(
                "Read", entityType.Name, httpContext.RequestAborted);

            visible.Add(pruned);
        }
        return Results.Json(visible);
    }
}
