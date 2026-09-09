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

            // Row-type definitions, so an AsDetail table can render its columns even when the row
            // type is absent from this catalogue — which is the common case, since a row edited
            // through its parent rarely has a Query grant of its own (#385).
            pruned = SubQueryPruner.EmbedDetailTypes(pruned, modelLoader);

            // ⚠️ Copy if neither step did. Both return the SAME reference when they change nothing
            // — that is their documented contract, and the reason they are safe — so the comment
            // that used to sit here ("PruneAsync already copied the definition") was false for
            // every type with no sub-queries. Writing the per-caller CanRead onto that reference
            // mutated ModelLoader's singleton graph process-wide, so two concurrent callers with
            // different rights could each serialize the other's answer.
            if (ReferenceEquals(pruned, entityType))
                pruned = entityType.ShallowCopy();

            pruned.CanRead = await permissionService.IsAllowedAsync(
                "Read", entityType.Name, httpContext.RequestAborted);

            visible.Add(pruned);
        }
        return Results.Json(visible);
    }
}
