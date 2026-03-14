using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MintPlayer.Spark.Authorization.Configuration;

namespace MintPlayer.Spark.Authorization.Services;

/// <summary>
/// Core service handling the OIDC authorization code flow with PKCE.
/// </summary>
internal class OidcClientService
{
    private readonly OidcProviderRegistry _registry;
    private readonly OidcDiscoveryService _discoveryService;
    private readonly IHttpClientFactory _httpClientFactory;

    public OidcClientService(
        OidcProviderRegistry registry,
        OidcDiscoveryService discoveryService,
        IHttpClientFactory httpClientFactory)
    {
        _registry = registry;
        _discoveryService = discoveryService;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Generates a PKCE code_verifier and code_challenge pair.
    /// </summary>
    public (string codeVerifier, string codeChallenge) GeneratePkce()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var codeVerifier = Base64UrlEncode(bytes);
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var codeChallenge = Base64UrlEncode(challengeBytes);
        return (codeVerifier, codeChallenge);
    }

    /// <summary>
    /// Builds the authorization URL for redirecting the user to the external provider.
    /// </summary>
    public async Task<string?> BuildAuthorizationUrlAsync(
        string scheme,
        string redirectUri,
        string state,
        string codeChallenge,
        CancellationToken ct = default)
    {
        var registration = _registry.GetByScheme(scheme);
        if (registration == null) return null;

        var authEndpoint = await ResolveAuthorizationEndpointAsync(registration, ct);
        if (authEndpoint == null) return null;

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = registration.Options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(" ", registration.Options.Scopes),
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };

        var queryString = string.Join("&",
            queryParams.Select(kvp =>
                $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

        return $"{authEndpoint}?{queryString}";
    }

    /// <summary>
    /// Exchanges an authorization code for tokens using the token endpoint.
    /// </summary>
    public async Task<OidcTokenResponse?> ExchangeCodeAsync(
        string scheme,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken ct = default)
    {
        var registration = _registry.GetByScheme(scheme);
        if (registration == null) return null;

        var tokenEndpoint = await ResolveTokenEndpointAsync(registration, ct);
        if (tokenEndpoint == null) return null;

        var client = _httpClientFactory.CreateClient();

        var formParams = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = registration.Options.ClientId,
            ["code_verifier"] = codeVerifier,
        };

        // Only include client_secret if configured (public clients may not have one)
        if (!string.IsNullOrEmpty(registration.Options.ClientSecret))
        {
            formParams["client_secret"] = registration.Options.ClientSecret;
        }

        var content = new FormUrlEncodedContent(formParams);
        var response = await client.PostAsync(tokenEndpoint, content, ct);

        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<OidcTokenResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    /// <summary>
    /// Fetches user info from the provider's userinfo endpoint.
    /// </summary>
    public async Task<Dictionary<string, JsonElement>?> GetUserInfoAsync(
        string scheme,
        string accessToken,
        CancellationToken ct = default)
    {
        var registration = _registry.GetByScheme(scheme);
        if (registration == null) return null;

        var userInfoEndpoint = await ResolveUserInfoEndpointAsync(registration, ct);
        if (userInfoEndpoint == null) return null;

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync(userInfoEndpoint, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
    }

    private async Task<string?> ResolveAuthorizationEndpointAsync(OidcProviderRegistration reg, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(reg.Options.AuthorizationEndpoint))
            return reg.Options.AuthorizationEndpoint;

        if (reg.ResolvedAuthorizationEndpoint != null)
            return reg.ResolvedAuthorizationEndpoint;

        if (reg.Options.Authority == null)
            return null;

        var doc = await _discoveryService.GetDiscoveryDocumentAsync(reg.Options.Authority, ct);
        if (doc == null) return null;

        reg.ResolvedAuthorizationEndpoint = doc.AuthorizationEndpoint;
        reg.ResolvedTokenEndpoint = doc.TokenEndpoint;
        reg.ResolvedUserInfoEndpoint = doc.UserInfoEndpoint;
        return reg.ResolvedAuthorizationEndpoint;
    }

    private async Task<string?> ResolveTokenEndpointAsync(OidcProviderRegistration reg, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(reg.Options.TokenEndpoint))
            return reg.Options.TokenEndpoint;

        if (reg.ResolvedTokenEndpoint != null)
            return reg.ResolvedTokenEndpoint;

        if (reg.Options.Authority == null)
            return null;

        var doc = await _discoveryService.GetDiscoveryDocumentAsync(reg.Options.Authority, ct);
        if (doc == null) return null;

        reg.ResolvedAuthorizationEndpoint = doc.AuthorizationEndpoint;
        reg.ResolvedTokenEndpoint = doc.TokenEndpoint;
        reg.ResolvedUserInfoEndpoint = doc.UserInfoEndpoint;
        return reg.ResolvedTokenEndpoint;
    }

    private async Task<string?> ResolveUserInfoEndpointAsync(OidcProviderRegistration reg, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(reg.Options.UserInfoEndpoint))
            return reg.Options.UserInfoEndpoint;

        if (reg.ResolvedUserInfoEndpoint != null)
            return reg.ResolvedUserInfoEndpoint;

        if (reg.Options.Authority == null)
            return null;

        var doc = await _discoveryService.GetDiscoveryDocumentAsync(reg.Options.Authority, ct);
        if (doc == null) return null;

        reg.ResolvedAuthorizationEndpoint = doc.AuthorizationEndpoint;
        reg.ResolvedTokenEndpoint = doc.TokenEndpoint;
        reg.ResolvedUserInfoEndpoint = doc.UserInfoEndpoint;
        return reg.ResolvedUserInfoEndpoint;
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Represents the token response from an OIDC provider.
/// </summary>
internal class OidcTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; set; }
}
