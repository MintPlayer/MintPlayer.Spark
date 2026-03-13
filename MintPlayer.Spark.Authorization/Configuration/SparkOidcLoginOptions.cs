namespace MintPlayer.Spark.Authorization.Configuration;

/// <summary>
/// Configuration options for an OIDC external login provider.
/// </summary>
public class SparkOidcLoginOptions
{
    /// <summary>
    /// OIDC Authority URL. Discovery document fetched from {Authority}/.well-known/openid-configuration.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>
    /// Manual authorization endpoint (for providers without standard OIDC discovery).
    /// </summary>
    public string? AuthorizationEndpoint { get; set; }

    /// <summary>
    /// Manual token endpoint (for providers without standard OIDC discovery).
    /// </summary>
    public string? TokenEndpoint { get; set; }

    /// <summary>
    /// Manual user info endpoint (for providers without standard OIDC discovery).
    /// </summary>
    public string? UserInfoEndpoint { get; set; }

    /// <summary>
    /// OAuth2 client identifier.
    /// </summary>
    public string ClientId { get; set; } = "";

    /// <summary>
    /// OAuth2 client secret. May be null for public clients using PKCE only.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Scopes to request from the provider.
    /// </summary>
    public List<string> Scopes { get; set; } = ["openid", "profile", "email"];

    /// <summary>
    /// Human-readable display name for the provider (shown in UI).
    /// </summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Optional icon identifier for the provider (e.g., "google", "microsoft").
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Claim type mappings from external provider claim types to local claim types.
    /// For example: { "sub" : "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier" }
    /// </summary>
    public Dictionary<string, string> ClaimMappings { get; set; } = new();
}
