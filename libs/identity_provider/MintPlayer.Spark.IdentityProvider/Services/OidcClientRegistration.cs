using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>Client metadata (RFC 7591 §2, OIDC Dynamic Client Registration §2), as JSON.</summary>
public sealed record OidcClientMetadata
{
    [JsonPropertyName("redirect_uris")] public List<string>? RedirectUris { get; init; }
    [JsonPropertyName("post_logout_redirect_uris")] public List<string>? PostLogoutRedirectUris { get; init; }
    [JsonPropertyName("client_name")] public string? ClientName { get; init; }
    [JsonPropertyName("token_endpoint_auth_method")] public string? TokenEndpointAuthMethod { get; init; }
    [JsonPropertyName("grant_types")] public List<string>? GrantTypes { get; init; }
    [JsonPropertyName("response_types")] public List<string>? ResponseTypes { get; init; }
    [JsonPropertyName("scope")] public string? Scope { get; init; }
    [JsonPropertyName("jwks")] public System.Text.Json.JsonElement? Jwks { get; init; }
    [JsonPropertyName("jwks_uri")] public string? JwksUri { get; init; }
    [JsonPropertyName("logo_uri")] public string? LogoUri { get; init; }
    [JsonPropertyName("client_uri")] public string? ClientUri { get; init; }
    [JsonPropertyName("policy_uri")] public string? PolicyUri { get; init; }
    [JsonPropertyName("tos_uri")] public string? TosUri { get; init; }
    [JsonPropertyName("contacts")] public List<string>? Contacts { get; init; }
    [JsonPropertyName("subject_type")] public string? SubjectType { get; init; }
    [JsonPropertyName("sector_identifier_uri")] public string? SectorIdentifierUri { get; init; }
    [JsonPropertyName("id_token_signed_response_alg")] public string? IdTokenSignedResponseAlg { get; init; }
    [JsonPropertyName("id_token_encrypted_response_alg")] public string? IdTokenEncryptedResponseAlg { get; init; }
    [JsonPropertyName("id_token_encrypted_response_enc")] public string? IdTokenEncryptedResponseEnc { get; init; }
    [JsonPropertyName("userinfo_signed_response_alg")] public string? UserinfoSignedResponseAlg { get; init; }
    [JsonPropertyName("userinfo_encrypted_response_alg")] public string? UserinfoEncryptedResponseAlg { get; init; }
    [JsonPropertyName("userinfo_encrypted_response_enc")] public string? UserinfoEncryptedResponseEnc { get; init; }
    [JsonPropertyName("default_max_age")] public int? DefaultMaxAge { get; init; }
    [JsonPropertyName("require_pushed_authorization_requests")] public bool? RequirePushedAuthorizationRequests { get; init; }
    [JsonPropertyName("require_signed_request_object")] public bool? RequireSignedRequestObject { get; init; }
    [JsonPropertyName("dpop_bound_access_tokens")] public bool? DpopBoundAccessTokens { get; init; }
    [JsonPropertyName("tls_client_certificate_bound_access_tokens")] public bool? TlsClientCertificateBoundAccessTokens { get; init; }
    [JsonPropertyName("tls_client_auth_subject_dn")] public string? TlsClientAuthSubjectDn { get; init; }
    [JsonPropertyName("backchannel_logout_uri")] public string? BackchannelLogoutUri { get; init; }
    [JsonPropertyName("backchannel_logout_session_required")] public bool? BackchannelLogoutSessionRequired { get; init; }
    [JsonPropertyName("frontchannel_logout_uri")] public string? FrontchannelLogoutUri { get; init; }
    [JsonPropertyName("frontchannel_logout_session_required")] public bool? FrontchannelLogoutSessionRequired { get; init; }
}

/// <summary>
/// Dynamic client registration (RFC 7591, RFC 7592, <c>docs/identity_provider_platform_PRD.md</c> D8),
/// gated: registering needs an initial access token, which only an approved developer can obtain
/// (from the portal). A registered application is that developer's, Admin role, in Development mode,
/// and passes exactly the rules the admin screen applies.
/// </summary>
internal sealed class OidcClientRegistration(Configuration.SparkIdentityProviderOptions options)
{
    /// <summary>How long an initial access token is valid.</summary>
    public static readonly TimeSpan InitialTokenLifetime = TimeSpan.FromDays(1);

    /// <summary>Issues an initial access token for <paramref name="developerId"/>; the plaintext is returned once.</summary>
    public static async Task<string> IssueInitialAccessTokenAsync(IAsyncDocumentSession session, string developerId, CancellationToken ct)
    {
        var token = OidcTokenReference.GenerateValue();
        await session.StoreExpiringAsync(new OidcToken
        {
            Id = OidcTokenReference.DocumentId("iat:" + token),
            Type = OidcTokenTypes.RegistrationAccessToken,
            Subject = developerId,
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(InitialTokenLifetime),
            Properties = { ["kind"] = "initial" },
        }, ct);
        return token;
    }

    /// <summary>The bearer token of a registration request, resolved: an initial access token (registering) or a client's registration access token (managing it).</summary>
    public static async Task<OidcToken?> ResolveBearerAsync(IAsyncDocumentSession session, HttpRequest request, string kind, CancellationToken ct)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        var token = await session.LoadAsync<OidcToken>(OidcTokenReference.DocumentId((kind == "initial" ? "iat:" : "rat:") + header[7..].Trim()), ct);
        return token is { Type: OidcTokenTypes.RegistrationAccessToken, Status: "valid" } && token.ExpiresAt > DateTime.UtcNow
            && token.Properties.GetValueOrDefault("kind") == kind ? token : null;
    }

    /// <summary>Applies <paramref name="metadata"/> to <paramref name="app"/>, then the admin screen's rules (throws <c>SparkValidationException</c>).</summary>
    public void Apply(OidcApplication app, OidcClientMetadata metadata)
    {
        app.DisplayName = string.IsNullOrWhiteSpace(metadata.ClientName) ? app.ClientId : metadata.ClientName.Trim();
        app.RedirectUris = metadata.RedirectUris ?? [];
        app.PostLogoutRedirectUris = metadata.PostLogoutRedirectUris ?? [];
        app.TokenEndpointAuthMethod = metadata.TokenEndpointAuthMethod ?? OidcClientAuthMethods.SecretBasic;
        app.ClientType = app.TokenEndpointAuthMethod == OidcClientAuthMethods.None ? "public" : "confidential";
        app.AllowedGrantTypes = metadata.GrantTypes ?? ["authorization_code"];
        app.Jwks = metadata.Jwks?.GetRawText();
        app.JwksUri = metadata.JwksUri;
        app.LogoUrl = metadata.LogoUri;
        app.HomepageUrl = metadata.ClientUri;
        app.PrivacyPolicyUrl = metadata.PolicyUri;
        app.TermsOfServiceUrl = metadata.TosUri;
        app.SupportEmail = metadata.Contacts?.FirstOrDefault();
        app.SubjectType = metadata.SubjectType ?? "public";
        app.SectorIdentifierUri = metadata.SectorIdentifierUri;
        app.IdTokenSignedResponseAlg = metadata.IdTokenSignedResponseAlg;
        app.IdTokenEncryptedResponseAlg = metadata.IdTokenEncryptedResponseAlg;
        app.IdTokenEncryptedResponseEnc = metadata.IdTokenEncryptedResponseEnc;
        app.UserinfoSignedResponseAlg = metadata.UserinfoSignedResponseAlg;
        app.UserinfoEncryptedResponseAlg = metadata.UserinfoEncryptedResponseAlg;
        app.UserinfoEncryptedResponseEnc = metadata.UserinfoEncryptedResponseEnc;
        app.DefaultMaxAge = metadata.DefaultMaxAge;
        app.RequirePushedAuthorizationRequests = metadata.RequirePushedAuthorizationRequests ?? false;
        app.RequireSignedRequestObject = metadata.RequireSignedRequestObject ?? false;
        app.RequireDpop = metadata.DpopBoundAccessTokens ?? false;
        app.TlsClientCertificateBoundAccessTokens = metadata.TlsClientCertificateBoundAccessTokens ?? false;
        app.TlsClientAuthSubjectDn = metadata.TlsClientAuthSubjectDn;
        app.BackChannelLogoutUri = metadata.BackchannelLogoutUri;
        app.BackChannelLogoutSessionRequired = metadata.BackchannelLogoutSessionRequired ?? false;
        app.FrontChannelLogoutUri = metadata.FrontchannelLogoutUri;
        app.FrontChannelLogoutSessionRequired = metadata.FrontchannelLogoutSessionRequired ?? false;

        var requested = (metadata.Scope ?? "openid").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Kept scopes keep their decided status; new ones are decided by MarkCrossOwnerScopesAsync.
        app.Scopes = requested
            .Select(name => app.Scopes.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? new OidcApplicationScope { Name = name, Required = name == "openid" })
            .ToList();

        if (metadata.ResponseTypes is { } responseTypes && responseTypes.Any(r => r != "code"))
            throw new MintPlayer.Spark.Abstractions.SparkValidationException("Only the 'code' response type is supported.", "response_types");

        Interceptors.OidcApplicationInterceptors.ValidateDefinition(app, options);
    }

    /// <summary>The metadata as registered (RFC 7591 §3.2.1).</summary>
    public static Dictionary<string, object?> Describe(OidcApplication app, string issuer) => new()
    {
        ["client_id"] = app.ClientId,
        ["client_id_issued_at"] = app.CreatedAt is { } created ? new DateTimeOffset(created, TimeSpan.Zero).ToUnixTimeSeconds() : null,
        ["client_name"] = app.DisplayName,
        ["redirect_uris"] = app.RedirectUris,
        ["post_logout_redirect_uris"] = app.PostLogoutRedirectUris,
        ["token_endpoint_auth_method"] = app.TokenEndpointAuthMethod,
        ["grant_types"] = app.AllowedGrantTypes,
        ["response_types"] = new[] { "code" },
        ["scope"] = string.Join(' ', app.ScopeNames()),
        ["jwks_uri"] = app.JwksUri,
        ["logo_uri"] = app.LogoUrl,
        ["client_uri"] = app.HomepageUrl,
        ["policy_uri"] = app.PrivacyPolicyUrl,
        ["tos_uri"] = app.TermsOfServiceUrl,
        ["subject_type"] = app.SubjectType,
        ["registration_client_uri"] = $"{issuer}/connect/register/{Uri.EscapeDataString(app.ClientId)}",
    };
}
