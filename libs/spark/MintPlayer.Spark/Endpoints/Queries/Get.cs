using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Abstractions.Prerendering;

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
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false), SparkPrerenderableAttribute.Instance);
    }

    // No antiforgery metadata, deliberately: the verb changed, what the endpoint does did not. See
    // the note in PersistentObject/Get.cs.

    // The check and the metadata shaping live in the catalogue, shared with the MCP tools (MCP PRD D9).
    [Inject] private readonly ISparkCatalogue catalogue;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>A body that cannot be bound gets the refusal an unusable request gets below, never a parse error (PRD D3a).</summary>
    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(SparkDenial.NotFoundOrSignIn(context, "Query not found"));

    public override async Task<IResult> HandleAsync(GetQueryRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        var id = request.QueryId;

        // A malformed body and an unknown query get the same answer, for the same reason the
        // unauthorized case does below: the status must not tell a caller which query ids are real.
        if (string.IsNullOrEmpty(id))
            return SparkDenial.NotFoundOrSignIn(httpContext, "Query not found");

        // Not 403 when the caller isn't authorized — so existence isn't leaked: the catalogue answers an
        // unknown query and a refused one with the same null. Like queries/execute (MCP PRD D18), an
        // anonymous caller on a host that can sign in gets the uniform 401 so it is sent to sign in; a
        // signed-in caller keeps the 404.
        var query = await catalogue.GetQueryAsync(id, httpContext.RequestAborted);
        return query is null
            ? SparkDenial.NotFoundOrSignIn(httpContext, $"Query '{id}' not found")
            : Results.Json(query);
    }
}
