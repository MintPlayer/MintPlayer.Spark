using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.EntityTypes;

[MemberOf<EntityTypesGroup>]
internal sealed partial class GetEntityType : IGetEndpoint
{
    public static string Path => "/{id}";

    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IAttributeRightsEnforcement attributeRights;
    [Inject] private readonly ILogger<GetEntityType> logger;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var id = httpContext.Request.RouteValues["id"]!.ToString()!;
        var entityType = modelLoader.ResolveEntityType(id);

        if (entityType is null)
            return Results.Json(new { error = $"Entity type '{id}' not found" }, statusCode: 404);

        // 404 rather than 403 — so existence isn't leaked to callers without Query rights.
        // (A Read grant passes too: Read implies Query at rights-expansion time, #324.)
        if (!await permissionService.IsAllowedAsync("Query", entityType.Name, httpContext.RequestAborted))
            return Results.Json(new { error = $"Entity type '{id}' not found" }, statusCode: 404);

        var pruned = await SubQueryPruner.PruneAsync(
            entityType, queryLoader, permissionService, logger, httpContext.RequestAborted);

        // Same reason as List.cs: the row types an AsDetail attribute names are usually absent from
        // the catalogue, so the client cannot resolve their columns from it (#385).
        // Per caller (contributions M2c-2a): Read-denied attributes absent, Edit-denied read-only, on
        // the type and on each embedded detail type by its own rights. Always a copy when it changes
        // anything — the loader's definitions are shared process-wide.
        // `?for=new` shapes it for the create form instead (#264, G1/G2): New-denied attributes absent
        // too, and no Edit deny applied. It only ever narrows what is seen: Read-denied stays absent.
        return Results.Json(await attributeRights.ForFormAsync(
            SubQueryPruner.EmbedDetailTypes(pruned, modelLoader),
            FormVerb(httpContext.Request.Query["for"].ToString()),
            httpContext.RequestAborted));
    }

    /// <summary><c>new</c>, <c>edit</c> or <c>read</c>; anything else is the edit shape, the default.</summary>
    private static string FormVerb(string purpose) => purpose.ToLowerInvariant() switch
    {
        "new" => SparkCoreActions.New,
        "read" => SparkCoreActions.Read,
        _ => SparkCoreActions.Edit,
    };
}
