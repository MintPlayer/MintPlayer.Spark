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
    [Inject] private readonly OidcKeyRing signingKeys;
    [Inject] private readonly MintPlayer.Spark.Services.ICultureLoader cultures;
    [Inject] private readonly OidcSurface surface;

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

        // D8: everything the provider supports is advertised, and nothing it does not. What it supports is its surface
        // (Flows, Endpoints): an endpoint or grant left out is not mapped, so it is not listed either. The keys keep the
        // order they always had.
        string[] clientAuth = [.. OidcClientAuthMethods.All.Where(m => m != OidcClientAuthMethods.None)];
        string[] signingAlgs = [.. signingKeys.SupportedAlgorithms];
        var authorize = surface.AuthorizationCode;
        var document = new Dictionary<string, object?> { ["issuer"] = issuer };
        AddIf(document, authorize, "authorization_endpoint", $"{issuer}/connect/authorize");
        document["token_endpoint"] = $"{issuer}/connect/token";
        AddIf(document, surface.UserInfo, "userinfo_endpoint", OidcTokenGenerator.UserInfoAudience(issuer));
        AddIf(document, surface.EndSession, "end_session_endpoint", $"{issuer}/connect/logout");
        AddIf(document, surface.Introspection, "introspection_endpoint", $"{issuer}/connect/introspect");
        AddIf(document, surface.Revocation, "revocation_endpoint", $"{issuer}/connect/revoke");
        AddIf(document, surface.PushedAuthorization, "pushed_authorization_request_endpoint", $"{issuer}/connect/par");
        AddIf(document, surface.DeviceCode, "device_authorization_endpoint", $"{issuer}/connect/device_authorization");
        AddIf(document, surface.Registration, "registration_endpoint", $"{issuer}/connect/register");
        document["jwks_uri"] = $"{issuer}/.well-known/jwks";
        document["response_types_supported"] = authorize ? new[] { "code" } : Array.Empty<string>();
        AddIf(document, authorize, "response_modes_supported", OidcAuthorizationResponse.Supported);
        document["grant_types_supported"] = surface.GrantTypes;
        document["subject_types_supported"] = new[] { "public", "pairwise" };
        document["id_token_signing_alg_values_supported"] = signingAlgs;
        document["id_token_encryption_alg_values_supported"] = OidcJwe.KeyAlgorithms;
        document["id_token_encryption_enc_values_supported"] = OidcJwe.ContentAlgorithms;
        AddIf(document, surface.UserInfo, "userinfo_signing_alg_values_supported", signingAlgs);
        AddIf(document, surface.UserInfo, "userinfo_encryption_alg_values_supported", OidcJwe.KeyAlgorithms);
        AddIf(document, surface.UserInfo, "userinfo_encryption_enc_values_supported", OidcJwe.ContentAlgorithms);
        AddIf(document, authorize, "request_object_signing_alg_values_supported", new[] { "RS256", "PS256", "ES256" });
        document["scopes_supported"] = scopeNames;
        document["claims_supported"] = claimsSupported;
        AddIf(document, authorize, "claims_parameter_supported", true);
        AddIf(document, authorize, "request_parameter_supported", true);
        // Only the request_uri values /connect/par hands out; no fetching of client-hosted request objects.
        AddIf(document, authorize, "request_uri_parameter_supported", false);
        AddIf(document, authorize, "require_request_uri_registration", true);
        AddIf(document, authorize, "acr_values_supported", OidcAcr.Supported);
        AddIf(document, authorize, "prompt_values_supported", new[] { "none", "login", "consent", "select_account" });
        AddIf(document, surface.Interactive, "ui_locales_supported", cultures.GetCulture().Languages.Keys.ToArray());
        AddIf(document, authorize, "code_challenge_methods_supported", new[] { "S256" });
        document["token_endpoint_auth_methods_supported"] = clientAuth;
        document["token_endpoint_auth_signing_alg_values_supported"] = new[] { "RS256", "PS256", "ES256" };
        AddIf(document, surface.Introspection, "introspection_endpoint_auth_methods_supported", clientAuth);
        AddIf(document, surface.Revocation, "revocation_endpoint_auth_methods_supported", clientAuth);
        document["tls_client_certificate_bound_access_tokens"] = true;
        document["dpop_signing_alg_values_supported"] = OidcProofOfPossession.DpopAlgorithms;
        AddIf(document, authorize, "authorization_response_iss_parameter_supported", true);
        AddIf(document, surface.EndSession, "backchannel_logout_supported", true);
        AddIf(document, surface.EndSession, "backchannel_logout_session_supported", true);
        AddIf(document, surface.EndSession, "frontchannel_logout_supported", true);
        AddIf(document, surface.EndSession, "frontchannel_logout_session_supported", true);

        return Results.Json(document);
    }

    private static void AddIf(Dictionary<string, object?> document, bool offered, string key, object value)
    {
        if (offered)
            document[key] = value;
    }

    /// <summary>Claims every id_token carries, whatever the scopes (<c>at_hash</c> and <c>auth_time</c> when known).</summary>
    internal static readonly string[] AlwaysEmittedClaims = ["sub", "iss", "aud", "exp", "iat", "nonce", "at_hash", "auth_time", "azp", "amr", "acr", "sid"];

    /// <summary>The scope claim types <see cref="OidcTokenGenerator"/> has a source for.</summary>
    internal static readonly string[] ScopeDrivenClaims =
        ["name", "preferred_username", "given_name", "family_name", "email", "email_verified", "role"];
}
