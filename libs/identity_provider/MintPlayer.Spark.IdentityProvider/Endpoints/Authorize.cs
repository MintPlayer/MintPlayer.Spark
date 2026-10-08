using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>The authorization endpoint (<c>GET /connect/authorize</c>).</summary>
/// <remarks>
/// Every value is optional at the binding level: a missing one is answered by
/// <see cref="OidcAuthorizeHandler"/> in RFC 6749 §4.1.2.1's shape, never by a binder's problem details.
/// </remarks>
[MemberOf<OidcConnectGroup>]
internal sealed partial class OidcAuthorize : IGetEndpoint<string>
{
    public static string Path => "/authorize";

    [Inject] private readonly OidcAuthorizeHandler handler;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var context = httpContextAccessor.HttpContext!;
        return handler.HandleAsync(context, OidcAuthorizeParameters.FromQuery(context.Request.Query), ct);
    }
}

/// <summary>
/// The authorization endpoint by POST (OIDC Core §3.1.2.1: "MUST support the use of the HTTP GET and
/// POST methods"). The client's own page posts it cross-site, so it carries no antiforgery token, by
/// design: nothing is decided on this request that the GET could not decide too.
/// </summary>
[MemberOf<OidcConnectGroup>]
internal sealed partial class OidcAuthorizeByPost : IPostEndpoint<OidcAuthorizeParameters>
{
    public static string Path => "/authorize";

    [Inject] private readonly OidcAuthorizeHandler handler;

    private HttpContext context = null!;

    protected override async ValueTask<OidcAuthorizeParameters?> BindRequestAsync(HttpContext context)
    {
        this.context = context;
        if (!context.Request.HasFormContentType)
            throw new EndpointBindingException(StatusCodes.Status400BadRequest, "Content-Type must be application/x-www-form-urlencoded.");
        return OidcAuthorizeParameters.FromForm(await context.Request.ReadFormAsync(context.RequestAborted));
    }

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(Results.Json(new { error = "invalid_request", error_description = failure?.Message }, statusCode: StatusCodes.Status400BadRequest));

    public override Task<IResult> HandleAsync(OidcAuthorizeParameters request, CancellationToken ct)
        => handler.HandleAsync(context, request, ct);
}
