using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Permissions;

internal sealed partial class GetPermissions : IGetEndpoint, IMemberOf<SparkGroup>
{
    public static string Path => "/permissions/{entityTypeId}";

    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IModelLoader modelLoader;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var entityTypeId = (string)httpContext.Request.RouteValues["entityTypeId"]!;

        // ClrType is a fallback rather than part of ResolveEntityType, deliberately. That helper has
        // a dozen call sites — every persistent-object and custom-action endpoint — and widening it
        // would make POST /spark/po/{ClrType}/... route, plus create a collision class where a CLR
        // name equal to another type's alias silently wins. Here it is safe and needed: an AsDetail
        // attribute names its row type by CLR name and by nothing else, so a client asking about a
        // row type has no other key to ask with (#385). No existence oracle either — the unknown
        // answer below is byte-identical to the fully-denied one.
        var entityType = modelLoader.ResolveEntityType(entityTypeId)
            ?? modelLoader.GetEntityTypeByClrType(entityTypeId);
        if (entityType is null)
        {
            // This endpoint is deliberately anonymous-callable (audit M-1), so it closes
            // its half of the existence oracle the other way: an unknown type answers
            // exactly what a fully denied one answers -- every right false -- rather than
            // 404. A 401 here would break the boot path for anonymous visitors.
            return Results.Json(new { canQuery = false, canRead = false, canCreate = false, canEdit = false, canDelete = false });
        }

        var target = entityType.Name;

        // canQuery is reported alongside the rest because Query and Read are independently
        // grantable: 'Query/Person' alone lists rows while refusing a by-id load, and 'Read/Person'
        // alone does the reverse. The combined 'QueryRead' bundles them invisibly, so the one right
        // it adds beyond a reader's expectation was precisely the one introspection never mentioned
        // — a client could not tell "no grid" from "no permissions endpoint" (#298).
        var canQuery = await permissionService.IsAllowedAsync("Query", target);
        var canRead = await permissionService.IsAllowedAsync("Read", target);
        var canCreate = await permissionService.IsAllowedAsync("New", target);
        var canEdit = await permissionService.IsAllowedAsync("Edit", target);
        var canDelete = await permissionService.IsAllowedAsync("Delete", target);

        return Results.Json(new { canQuery, canRead, canCreate, canEdit, canDelete });
    }
}
