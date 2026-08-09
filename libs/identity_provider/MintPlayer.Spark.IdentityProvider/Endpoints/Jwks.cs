using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

internal static class Jwks
{
    public static async Task Handle(HttpContext context)
    {
        var signingKeyService = context.RequestServices.GetRequiredService<OidcSigningKeyService>();
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

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(jwks);
    }
}
