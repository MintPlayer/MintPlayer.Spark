using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;

// The protocol endpoints that carry no CORS convention.

/// <summary>The authorization endpoint.</summary>
[MemberOf<OidcConnectGroup>]
internal sealed class OidcAuthorize : IGetEndpoint
{
    public static string Path => "/authorize";

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        await Authorize.Handle(httpContext);
        return Results.Empty;
    }
}

/// <summary>Renders the consent screen.</summary>
[MemberOf<OidcConnectGroup>]
internal sealed class OidcConsentPage : IGetEndpoint
{
    public static string Path => "/consent";

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        await Consent.HandleGet(httpContext);
        return Results.Empty;
    }
}

/// <summary>Accepts the consent screen.</summary>
[MemberOf<OidcConnectGroup>]
internal sealed class OidcConsentSubmit : IPostEndpoint
{
    public static string Path => "/consent";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        await Consent.HandlePost(httpContext);
        return Results.Empty;
    }
}

/// <summary>Lists the applications the signed-in user has authorized.</summary>
[MemberOf<OidcConnectGroup>]
internal sealed class OidcConnectedApplications : IGetEndpoint
{
    public static string Path => "/applications";

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        await ConnectedApplications.HandleGet(httpContext);
        return Results.Empty;
    }
}

/// <summary>Revokes one application's authorization.</summary>
[MemberOf<OidcConnectGroup>]
internal sealed class OidcRevokeApplication : IPostEndpoint
{
    public static string Path => "/applications/revoke";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        await ConnectedApplications.HandleRevoke(httpContext);
        return Results.Empty;
    }
}

/// <summary>The end-session endpoint.</summary>
[MemberOf<OidcConnectGroup>]
internal sealed class OidcLogout : IGetEndpoint
{
    public static string Path => "/logout";

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        await Logout.Handle(httpContext);
        return Results.Empty;
    }
}

/// <summary>Token introspection.</summary>
/// <remarks>
/// ⚠️ Deliberately NOT a member of <see cref="OidcConnectCorsGroup"/> — introspection never carried
/// the dynamic-CORS convention, unlike its neighbours in that group.
/// <para>
/// Also deliberately not antiforgery-protected: this is a machine endpoint authenticated by client
/// credentials, never by an ambient cookie, so there is no ambient authority for a cross-site
/// request to borrow.
/// </para>
/// </remarks>
[MemberOf<OidcConnectGroup>]
internal sealed class OidcIntrospect : IPostEndpoint
{
    public static string Path => "/introspect";

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        await Introspection.Handle(httpContext);
        return Results.Empty;
    }
}
