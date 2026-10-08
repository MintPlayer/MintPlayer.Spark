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

    [Inject] private readonly OidcSigningKeyService signingKeyService;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var jwk = signingKeyService.GetPublicJwk();

        var jwks = new
        {
            keys = new[]
            {
                new
                {
                    kty = jwk.Kty,
                    use = jwk.Use,
                    kid = jwk.Kid,
                    alg = jwk.Alg,
                    n = jwk.N,
                    e = jwk.E,
                }
            }
        };

        return Task.FromResult(Results.Json(jwks));
    }
}
