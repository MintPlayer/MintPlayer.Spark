using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>The signing keys, as a JWK set (<c>GET /.well-known/jwks</c>).</summary>
[MemberOf<OidcWellKnownGroup>]
internal sealed partial class OidcJwks : IGetEndpoint
{
    public static string Path => "/jwks";

    [Inject] private readonly OidcKeyRing signingKeyService;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        // Every published key: the next one before it signs, the active ones, and retired ones while
        // tokens they signed can still be presented (I10). Public halves only.
        var jwks = new
        {
            keys = signingKeyService.Jwks.Keys.Select(k => new Dictionary<string, string?>
            {
                ["kty"] = k.Kty, ["use"] = k.Use, ["kid"] = k.Kid, ["alg"] = k.Alg,
                ["n"] = k.N, ["e"] = k.E, ["crv"] = k.Crv, ["x"] = k.X, ["y"] = k.Y,
            }.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value)),
        };
        return Task.FromResult(Results.Json(jwks));
    }
}
