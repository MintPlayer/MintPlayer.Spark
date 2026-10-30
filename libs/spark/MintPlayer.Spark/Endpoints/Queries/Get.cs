using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.Spark.Abstractions;
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
    [Inject] private readonly IQueryExecutor queryExecutor;
    [Inject] private readonly Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions;
    [Inject] private readonly ILogger<GetQuery> logger;

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
        if (query is null)
            return SparkDenial.NotFoundOrSignIn(httpContext, $"Query '{id}' not found");

        // The query plus `resultColumns`, the columns its result will ship, so a server-rendered grid
        // draws its cells on the client's first pass rather than after its first fetch
        // (docs/spark_ssr_PRD.md D13). A JSON node rather than a property on SparkQuery: the catalogue
        // may hand back the loader's shared instance, and this is per caller.
        var options = jsonOptions.Value.SerializerOptions;
        var body = JsonSerializer.SerializeToNode(query, options)!.AsObject();
        if (await DescribeColumnsAsync(query, cancellationToken) is { } columns)
            body["resultColumns"] = JsonSerializer.SerializeToNode(columns, options);
        return Results.Json(body, options);
    }

    /// <summary>
    /// Null when the columns cannot be described: then the client takes them from the first fetch, as
    /// before, and the misconfiguration surfaces where it always has, on <c>queries/execute</c>.
    /// </summary>
    private async Task<IReadOnlyList<QueryColumn>?> DescribeColumnsAsync(SparkQuery query, CancellationToken cancellationToken)
    {
        try
        {
            return await queryExecutor.DescribeColumnsAsync(query, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Query '{Query}': no result columns in its metadata.", query.Name);
            return null;
        }
    }
}
