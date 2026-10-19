using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Consent;
using MintPlayer.Spark.Consent;

namespace MintPlayer.Spark.Endpoints.Consent;

/// <summary>
/// <c>/spark/consent</c> (commerce PRD X21): the banner's and settings page's state and choices, and the unsubscribe
/// link of marketing mail. Anonymous by design: a guest answers the banner. Every endpoint answers 404 until
/// <c>spark.AddConsent()</c> registered the primitive.
/// </summary>
[MemberOf<SparkGroup>]
internal class ConsentGroup : IEndpointGroup
{
    public static string Prefix => "/consent";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services)
        => group.AllowAnonymous();
}

/// <summary><c>GET /spark/consent/</c>: the declared purposes and the caller's decisions.</summary>
[MemberOf<ConsentGroup>]
internal sealed partial class GetConsentState : IGetEndpoint
{
    public static string Path => "/";

    [Inject] private readonly ISparkConsent? consent;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (consent is null)
            return Results.NotFound();
        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Json(await consent.GetStateAsync(httpContext.RequestAborted));
    }
}

/// <summary>
/// <c>POST /spark/consent/</c>: records the caller's decisions (banner, settings page). Held to antiforgery like every
/// mutating call: a cross-site page must not be able to grant consent in the visitor's name.
/// </summary>
[MemberOf<ConsentGroup>]
internal sealed partial class RecordConsent : IPostEndpoint<SparkConsentChoice>
{
    public static string Path => "/";

    [Inject] private readonly ISparkConsent? consent;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(SparkConsentChoice request, CancellationToken cancellationToken)
    {
        if (consent is null)
            return Results.NotFound();
        if (request?.Decisions is null || request.Decisions.Count > 64)
            return Results.BadRequest();
        httpContextAccessor.HttpContext!.Response.Headers.CacheControl = "no-store";
        return Results.Json(await consent.RecordAsync(request, cancellationToken));
    }
}

/// <summary><c>GET /spark/consent/unsubscribe?token=</c>: what the unsubscribe page shows. 404 for a bad token.</summary>
[MemberOf<ConsentGroup>]
internal sealed partial class DescribeUnsubscribe : IGetEndpoint
{
    public static string Path => "/unsubscribe";

    [Inject] private readonly ISparkConsent? consent;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (consent is not SparkConsentService service)
            return Results.NotFound();
        httpContext.Response.Headers.CacheControl = "no-store";
        var info = await service.DescribeUnsubscribeAsync(httpContext.Request.Query["token"], httpContext.RequestAborted);
        return info is null ? Results.NotFound() : Results.Json(info);
    }
}

/// <summary>
/// <c>POST /spark/consent/unsubscribe</c>: the unsubscribe page's single button (§4.20: one click, no sign-in). The
/// token is the credential, so there is no ambient authority to forge and no antiforgery check.
/// </summary>
[MemberOf<ConsentGroup>]
internal sealed partial class Unsubscribe : IPostEndpoint<SparkConsentUnsubscribeRequest>
{
    public static string Path => "/unsubscribe";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));

    [Inject] private readonly ISparkConsent? consent;

    public override async Task<IResult> HandleAsync(SparkConsentUnsubscribeRequest request, CancellationToken cancellationToken)
        => consent is SparkConsentService service && await service.UnsubscribeAsync(request?.Token, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();
}

/// <summary>
/// <c>POST /spark/consent/unsubscribe/one-click?token=</c>: the RFC 8058 target of <c>List-Unsubscribe-Post</c>. The mail
/// client POSTs <c>List-Unsubscribe=One-Click</c> without cookies; the body is not needed, the token in the URI decides.
/// A GET never unsubscribes (link scanners prefetch URLs; RFC 8058 §3.2), so only POST is mapped.
/// </summary>
[MemberOf<ConsentGroup>]
internal sealed partial class UnsubscribeOneClick : IEndpoint
{
    public static string Path => "/unsubscribe/one-click";

    public static IEnumerable<string> Methods => ["POST"];

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));

    [Inject] private readonly ISparkConsent? consent;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => consent is SparkConsentService service && await service.UnsubscribeAsync(httpContext.Request.Query["token"], httpContext.RequestAborted)
            ? Results.Ok()
            : Results.NotFound();
}
