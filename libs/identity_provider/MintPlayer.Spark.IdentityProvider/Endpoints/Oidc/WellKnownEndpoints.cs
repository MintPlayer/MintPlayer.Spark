using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;

// The sixteen OIDC endpoints are wrappers: each names a route and delegates to the static handler
// that already implements it. They are grouped one file per endpoint group rather than one file per
// class, because a twelve-line wrapper with no logic of its own reads better beside its siblings
// than alone - the same reasoning that already puts every group in one Groups.cs.
//
// ⚠️ The handlers return Task, not Task<IResult>: they write the response themselves. Results.Empty
// after the fact is a no-op, so behaviour is preserved exactly.

/// <summary>The OpenID Connect discovery document.</summary>
[MemberOf<OidcWellKnownGroup>]
internal sealed class OidcDiscovery : IGetEndpoint
{
    public static string Path => "/openid-configuration";

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        await Discovery.Handle(httpContext);
        return Results.Empty;
    }
}

/// <summary>The signing keys, as a JWK set.</summary>
[MemberOf<OidcWellKnownGroup>]
internal sealed class OidcJwks : IGetEndpoint
{
    public static string Path => "/jwks";

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        await Jwks.Handle(httpContext);
        return Results.Empty;
    }
}
