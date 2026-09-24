using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Queries;

internal sealed partial class GetQuery : IPostEndpoint, IMemberOf<QueriesGroup>
{
    public static string Path => "/get";

    // ⚠️ EXPLICITLY exempt, not merely unannotated. This is a read; a forged one changes nothing and
    // the attacker cannot see the response. It was exempt by absence until 11.0.0, when
    // SparkAntiforgeryOptions.RequireAntiforgery began defaulting to true and started gating any
    // mutating-verb request under /spark that carries an ambient credential — which swept these in
    // against the decision recorded above. Saying it out loud restores that decision and makes it
    // survive the next default change.
    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));
    }

    // No antiforgery metadata, deliberately: the verb changed, what the endpoint does did not. See
    // the note in PersistentObject/Get.cs.

    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IPermissionService permissionService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var request = await SparkRequestBody.ReadAsync<GetQueryRequest>(httpContext);
        var id = request?.QueryId;

        // A malformed body and an unknown query get the same answer, for the same reason the
        // unauthorized case does below: the status must not tell a caller which query ids are real.
        if (string.IsNullOrEmpty(id))
            return Results.Json(new { error = "Query not found" }, statusCode: 404);

        var query = queryLoader.ResolveQuery(id);

        if (query is null)
            return Results.Json(new { error = $"Query '{id}' not found" }, statusCode: 404);

        // Return 404 (not 403) when the caller isn't authorized — so existence isn't leaked.
        if (query.EntityType is null ||
            !await permissionService.IsAllowedAsync("Query", query.EntityType, httpContext.RequestAborted))
        {
            return Results.Json(new { error = $"Query '{id}' not found" }, statusCode: 404);
        }

        return Results.Json(query);
    }
}
