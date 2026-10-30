using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Abstractions.Prerendering;

namespace MintPlayer.Spark.Endpoints.Queries;

[MemberOf<QueriesGroup>]
internal sealed partial class ExecuteQuery : IPostEndpoint<ExecuteQueryRequest>
{
    public static string Path => "/execute";

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

    // The body lives in QueryReadPath, shared with the live socket (X4): a pushed page and a fetched
    // page are one read path, so every check below the HTTP layer applies to both.
    [Inject] private readonly IQueryReadPath readPath;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>A body that cannot be bound gets the refusal an unusable request gets below, never a parse error (PRD D3a).</summary>
    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(NotFound(context, "Query not found"));

    public override async Task<IResult> HandleAsync(ExecuteQueryRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        var outcome = await readPath.ExecuteAsync(request, httpContext.RequestAborted);
        if (outcome.Result is { } result)
            return Results.Json(result);

        return outcome.StatusCode == StatusCodes.Status404NotFound
            ? NotFound(httpContext, outcome.Error)
            : Results.Json(new { error = outcome.Error }, statusCode: outcome.StatusCode);
    }

    /// <summary>
    /// Every not-found of the read path — unknown query, refused query, unreadable parent — as
    /// <see cref="SparkDenial"/> answers it on the other access endpoints (MCP PRD D18): an anonymous
    /// caller on a host that can sign in gets the uniform 401, so <c>ng-spark/auth</c> sends them to
    /// sign in. Uniform because every 404 maps alike, so a real query and a typo stay indistinguishable.
    /// A signed-in caller, or any caller of a host without sign-in, keeps the 404 and its message.
    /// </summary>
    private static IResult NotFound(HttpContext httpContext, string? error)
    {
        var (body, status) = SparkDenial.Refuse(httpContext);
        return status == StatusCodes.Status401Unauthorized
            ? Results.Json(body, statusCode: status)
            : Results.Json(new { error }, statusCode: StatusCodes.Status404NotFound);
    }
}
