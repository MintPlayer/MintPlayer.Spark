using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Authorization.Services;

/// <summary>
/// Fetches and caches OIDC discovery documents. Registered as a singleton.
/// </summary>
internal class OidcDiscoveryService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<string, OidcDiscoveryDocument> _cache = new();

    public OidcDiscoveryService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Fetches the OIDC discovery document from the authority's well-known endpoint.
    /// Results are cached in memory.
    /// </summary>
    public async Task<OidcDiscoveryDocument?> GetDiscoveryDocumentAsync(string authority, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(authority, out var cached))
            return cached;

        var client = _httpClientFactory.CreateClient();
        var url = authority.TrimEnd('/') + "/.well-known/openid-configuration";

        var response = await client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonSerializer.Deserialize<OidcDiscoveryDocument>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (doc != null)
            _cache[authority] = doc;

        return doc;
    }
}

/// <summary>
/// Represents the relevant fields from an OIDC discovery document.
/// </summary>
internal class OidcDiscoveryDocument
{
    [JsonPropertyName("authorization_endpoint")]
    public string? AuthorizationEndpoint { get; set; }

    [JsonPropertyName("token_endpoint")]
    public string? TokenEndpoint { get; set; }

    [JsonPropertyName("userinfo_endpoint")]
    public string? UserInfoEndpoint { get; set; }

    [JsonPropertyName("jwks_uri")]
    public string? JwksUri { get; set; }

    [JsonPropertyName("issuer")]
    public string? Issuer { get; set; }
}
