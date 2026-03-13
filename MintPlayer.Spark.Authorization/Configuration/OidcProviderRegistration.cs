namespace MintPlayer.Spark.Authorization.Configuration;

/// <summary>
/// Internal model storing a configured OIDC provider and its resolved endpoints.
/// </summary>
internal class OidcProviderRegistration
{
    public string Scheme { get; set; } = "";
    public SparkOidcLoginOptions Options { get; set; } = new();

    /// <summary>
    /// Resolved authorization endpoint (from discovery or manual config).
    /// </summary>
    public string? ResolvedAuthorizationEndpoint { get; set; }

    /// <summary>
    /// Resolved token endpoint (from discovery or manual config).
    /// </summary>
    public string? ResolvedTokenEndpoint { get; set; }

    /// <summary>
    /// Resolved user info endpoint (from discovery or manual config).
    /// </summary>
    public string? ResolvedUserInfoEndpoint { get; set; }
}
