using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;

// The protocol endpoints that carry the dynamic-CORS convention, via OidcConnectCorsGroup.
//
// ⚠️ Deliberately NOT antiforgery-protected: these are machine endpoints authenticated by client
// credentials, never by an ambient cookie, so there is no ambient authority for a cross-site request
// to borrow - and a token that has to be presented cannot be supplied by the browser on the caller's
// behalf. Requiring a token here would simply break every conforming OAuth client.

// The token endpoint is OidcTokenEndpoint<TUser> (Endpoints/Token.cs): generic over the user type, so it is
// closed and mapped by OidcUserEndpoints rather than by the generated mapping.

/// <summary>The userinfo endpoint.</summary>
[MemberOf<OidcConnectCorsGroup>]
internal sealed class OidcUserInfo : IGetEndpoint
{
    public static string Path => "/userinfo";

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        await UserInfo.Handle(httpContext);
        return Results.Empty;
    }
}

/// <summary>Token revocation.</summary>
[MemberOf<OidcConnectCorsGroup>]
internal sealed class OidcRevoke : IPostEndpoint
{
    public static string Path => "/revoke";

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        await Revocation.Handle(httpContext);
        return Results.Empty;
    }
}
