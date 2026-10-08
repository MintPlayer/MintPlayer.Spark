using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>The OpenID Connect discovery document (<c>GET /.well-known/openid-configuration</c>).</summary>
/// <remarks>Raw: it takes no input, and the issuer it advertises is resolved from the request.</remarks>
[MemberOf<OidcWellKnownGroup>]
internal sealed partial class OidcDiscovery : IGetEndpoint
{
    public static string Path => "/openid-configuration";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly OidcIssuer oidcIssuer;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var ct = httpContext.RequestAborted;

        // Must be the same value the tokens carry, or a relying party that discovers us here
        // will reject everything we mint.
        var issuer = oidcIssuer.Resolve(httpContext.Request);

        // Load scopes dynamically from DB
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

        return Results.Json(document);
    }
}
