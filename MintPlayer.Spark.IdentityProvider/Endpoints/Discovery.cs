using Microsoft.AspNetCore.Http;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

internal static class Discovery
{
    public static async Task Handle(HttpContext context)
    {
        var request = context.Request;
        var issuer = $"{request.Scheme}://{request.Host}";

        var document = new
        {
            issuer,
            authorization_endpoint = $"{issuer}/connect/authorize",
            token_endpoint = $"{issuer}/connect/token",
            userinfo_endpoint = $"{issuer}/connect/userinfo",
            end_session_endpoint = $"{issuer}/connect/logout",
            jwks_uri = $"{issuer}/.well-known/jwks",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
            scopes_supported = new[] { "openid", "profile", "email", "roles" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_post" },
        };

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(document);
    }
}
