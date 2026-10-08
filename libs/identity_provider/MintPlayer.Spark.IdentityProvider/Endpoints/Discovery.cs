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
    [Inject] private readonly OidcSigningKeyService signingKeys;
    [Inject] private readonly MintPlayer.Spark.Services.ICultureLoader cultures;

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

        // D8: everything the provider supports is advertised, and nothing it does not.
        string[] clientAuth = [.. OidcClientAuthMethods.All.Where(m => m != OidcClientAuthMethods.None)];
        string[] signingAlgs = [.. signingKeys.SupportedAlgorithms];
        var document = new Dictionary<string, object?>
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = $"{issuer}/connect/authorize",
            ["token_endpoint"] = $"{issuer}/connect/token",
            ["userinfo_endpoint"] = $"{issuer}/connect/userinfo",
            ["end_session_endpoint"] = $"{issuer}/connect/logout",
            ["introspection_endpoint"] = $"{issuer}/connect/introspect",
            ["revocation_endpoint"] = $"{issuer}/connect/revoke",
            ["pushed_authorization_request_endpoint"] = $"{issuer}/connect/par",
            ["device_authorization_endpoint"] = $"{issuer}/connect/device_authorization",
            ["registration_endpoint"] = $"{issuer}/connect/register",
            ["jwks_uri"] = $"{issuer}/.well-known/jwks",
            ["response_types_supported"] = new[] { "code" },
            ["response_modes_supported"] = OidcAuthorizationResponse.Supported,
            ["grant_types_supported"] = new[] { "authorization_code", "refresh_token", "client_credentials", OidcDeviceCodes.GrantType, "urn:ietf:params:oauth:grant-type:token-exchange" },
            ["subject_types_supported"] = new[] { "public", "pairwise" },
            ["id_token_signing_alg_values_supported"] = signingAlgs,
            ["id_token_encryption_alg_values_supported"] = OidcJwe.KeyAlgorithms,
            ["id_token_encryption_enc_values_supported"] = OidcJwe.ContentAlgorithms,
            ["userinfo_signing_alg_values_supported"] = signingAlgs,
            ["userinfo_encryption_alg_values_supported"] = OidcJwe.KeyAlgorithms,
            ["userinfo_encryption_enc_values_supported"] = OidcJwe.ContentAlgorithms,
            ["request_object_signing_alg_values_supported"] = new[] { "RS256", "PS256", "ES256" },
            ["scopes_supported"] = scopeNames,
            ["claims_supported"] = claimsSupported,
            ["claims_parameter_supported"] = true,
            ["request_parameter_supported"] = true,
            // Only the request_uri values /connect/par hands out; no fetching of client-hosted request objects.
            ["request_uri_parameter_supported"] = false,
            ["require_request_uri_registration"] = true,
            ["acr_values_supported"] = OidcAcr.Supported,
            ["prompt_values_supported"] = new[] { "none", "login", "consent", "select_account" },
            ["ui_locales_supported"] = cultures.GetCulture().Languages.Keys.ToArray(),
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["token_endpoint_auth_methods_supported"] = clientAuth,
            ["token_endpoint_auth_signing_alg_values_supported"] = new[] { "RS256", "PS256", "ES256" },
            ["introspection_endpoint_auth_methods_supported"] = clientAuth,
            ["revocation_endpoint_auth_methods_supported"] = clientAuth,
            ["tls_client_certificate_bound_access_tokens"] = true,
            ["dpop_signing_alg_values_supported"] = OidcProofOfPossession.DpopAlgorithms,
            ["authorization_response_iss_parameter_supported"] = true,
        };

        return Results.Json(document);
    }

    /// <summary>Claims every id_token carries, whatever the scopes (<c>at_hash</c> and <c>auth_time</c> when known).</summary>
    internal static readonly string[] AlwaysEmittedClaims = ["sub", "iss", "aud", "exp", "iat", "nonce", "at_hash", "auth_time", "azp", "amr", "acr", "sid"];

    /// <summary>The scope claim types <see cref="OidcTokenGenerator"/> has a source for.</summary>
    internal static readonly string[] ScopeDrivenClaims =
        ["name", "preferred_username", "given_name", "family_name", "email", "email_verified", "role"];
}
