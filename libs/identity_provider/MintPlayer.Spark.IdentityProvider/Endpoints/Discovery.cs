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

        var scopes = await OidcScopeCatalog.LoadDiscoverableAsync(session, ct);

        var scopeNames = scopes.Select(s => s.Name).ToArray();

        // The claims this provider can actually emit (OidcTokenGenerator), intersected with what the
        // advertised scopes ask for, plus the ones every id_token carries. A scope naming a claim the
        // generator has no source for is not advertised as supporting it.
        var claimsSupported = AlwaysEmittedClaims
            .Concat(scopes.SelectMany(s => s.ClaimTypes)
                .Where(c => ScopeDrivenClaims.Contains(c, StringComparer.OrdinalIgnoreCase))
                .Select(c => c.ToLowerInvariant()))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

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
            // The code is always delivered on the redirect's query string; there is no form_post.
            response_modes_supported = new[] { "query" },
            grant_types_supported = new[] { "authorization_code", "refresh_token", "client_credentials" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
            scopes_supported = scopeNames,
            claims_supported = claimsSupported,
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_post" },
            introspection_endpoint_auth_methods_supported = new[] { "client_secret_post" },
            revocation_endpoint_auth_methods_supported = new[] { "client_secret_post" },
        };

        return Results.Json(document);
    }

    /// <summary>Claims every id_token carries, whatever the scopes (<c>at_hash</c> and <c>auth_time</c> when known).</summary>
    internal static readonly string[] AlwaysEmittedClaims = ["sub", "iss", "aud", "exp", "iat", "nonce", "at_hash", "auth_time"];

    /// <summary>The scope claim types <see cref="OidcTokenGenerator"/> has a source for.</summary>
    internal static readonly string[] ScopeDrivenClaims =
        ["name", "preferred_username", "given_name", "family_name", "email", "email_verified", "role"];
}
