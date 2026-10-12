using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Queries;

[MemberOf<QueriesGroup>]
internal sealed partial class GetQuery : IPostEndpoint<GetQueryRequest>
{
    public static string Path => "/get";

    // ⚠️ EXPLICITLY exempt, not merely unannotated. This is a read; a forged one changes nothing and
    // the attacker cannot see the response. It was exempt by absence until 11.0.0, when
    // SparkAntiforgeryOptions.RequireAntiforgery began defaulting to true and started gating any
    // mutating-verb request under /spark that carries an ambient credential — which swept these in
    // against the decision recorded above. Saying it out loud restores that decision and makes it
    // survive the next default change.
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));
    }

    // No antiforgery metadata, deliberately: the verb changed, what the endpoint does did not. See
    // the note in PersistentObject/Get.cs.

    // The check and the metadata shaping live in the catalogue, shared with the MCP tools (MCP PRD D9).
    [Inject] private readonly ISparkCatalogue catalogue;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>A body that cannot be bound gets the refusal an unusable request gets below, never a parse error (PRD D3a).</summary>
    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(Results.Json(new { error = "Query not found" }, statusCode: 404));

    public override async Task<IResult> HandleAsync(GetQueryRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        var id = request.QueryId;

        // A malformed body and an unknown query get the same answer, for the same reason the
        // unauthorized case does below: the status must not tell a caller which query ids are real.
        if (string.IsNullOrEmpty(id))
            return Results.Json(new { error = "Query not found" }, statusCode: 404);

        // Return 404 (not 403) when the caller isn't authorized — so existence isn't leaked: the
        // catalogue answers an unknown query and a refused one with the same null.
        var query = await catalogue.GetQueryAsync(id, httpContext.RequestAborted);
        return query is null
            ? Results.Json(new { error = $"Query '{id}' not found" }, statusCode: 404)
            : Results.Json(query);
    }
}
