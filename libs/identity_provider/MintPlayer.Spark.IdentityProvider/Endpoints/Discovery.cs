using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

internal static class Discovery
{
    public static async Task Handle(HttpContext context)
    {
        var ct = context.RequestAborted;
        var request = context.Request;
        var issuer = $"{request.Scheme}://{request.Host}";

        // Load scopes dynamically from DB
        var store = context.RequestServices.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();

        var scopes = await session
            .Query<OidcScope>()
            .Where(s => s.ShowInDiscoveryDocument && s.Enabled)
            .ToListAsync(ct);

        var scopeNames = scopes.Select(s => s.Name).ToArray();

        var document = new
        {
            issuer,
            authorization_endpoint = $"{issuer}/connect/authorize",
            token_endpoint = $"{issuer}/connect/token",
            userinfo_endpoint = $"{issuer}/connect/userinfo",
            end_session_endpoint = $"{issuer}/connect/logout",
            introspection_endpoint = $"{issuer}/connect/introspect",
            revocation_endpoint = $"{issuer}/connect/revoke",
            jwks_uri = $"{issuer}/.well-known/jwks",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token", "client_credentials" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
            scopes_supported = scopeNames,
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_post" },
            introspection_endpoint_auth_methods_supported = new[] { "client_secret_post" },
            revocation_endpoint_auth_methods_supported = new[] { "client_secret_post" },
        };

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(document);
    }
}
