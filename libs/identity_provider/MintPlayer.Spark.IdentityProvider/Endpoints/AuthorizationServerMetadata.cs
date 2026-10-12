using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Mcp;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// RFC 8414 authorization server metadata (<c>GET /.well-known/oauth-authorization-server</c>), mapped only with
/// <c>idp.ForMcp()</c> (MCP PRD D5, gap G1): what an MCP client reads after the protected-resource document names this
/// server.
/// </summary>
/// <remarks>
/// It describes the agent's side of the provider only: code + PKCE (S256) for public clients, refresh rotation,
/// revocation, Client ID Metadata Documents, and anonymous registration when it is on. Codex uses a metadata document
/// only when <c>client_id_metadata_document_supported</c> is true and <c>none</c> is an auth method; Claude Code adds
/// <c>offline_access</c> when it is listed; Gemini requests every listed scope, which is harmless because the grant,
/// not the scope, decides (D6). The OpenID discovery document stays as it is, for relying parties.
/// </remarks>
[MemberOf<OidcMcpWellKnownGroup>]
internal sealed partial class OidcAuthorizationServerMetadata : IGetEndpoint
{
    public static string Path => "/oauth-authorization-server";

    [Inject] private readonly OidcIssuer oidcIssuer;
    [Inject] private readonly SparkIdentityProviderOptions options;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var issuer = oidcIssuer.Resolve(httpContext.Request);
        var document = new Dictionary<string, object?>
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = $"{issuer}/connect/authorize",
            ["token_endpoint"] = $"{issuer}/connect/token",
            ["revocation_endpoint"] = $"{issuer}/connect/revoke",
            ["jwks_uri"] = $"{issuer}/.well-known/jwks",
            ["response_types_supported"] = new[] { "code" },
            ["response_modes_supported"] = OidcAuthorizationResponse.Supported,
            ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["token_endpoint_auth_methods_supported"] = OidcClientAuthMethods.All,
            ["revocation_endpoint_auth_methods_supported"] = OidcClientAuthMethods.All,
            ["scopes_supported"] = OidcAgentClients.Scopes,
            ["client_id_metadata_document_supported"] = true,
            ["authorization_response_iss_parameter_supported"] = true,
        };
        if (options.Mcp is { DynamicRegistration: true })
            document["registration_endpoint"] = $"{issuer}/connect/register";

        httpContext.Response.Headers.CacheControl = "public, max-age=300";
        return Task.FromResult(Results.Json(document));
    }
}
