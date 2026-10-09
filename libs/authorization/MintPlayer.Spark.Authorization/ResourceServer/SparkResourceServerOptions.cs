namespace MintPlayer.Spark.Authorization.ResourceServer;

/// <summary>
/// How a Spark application accepts access tokens as a resource server (<c>spark.AddSparkResourceServer(...)</c>,
/// <c>docs/identity_provider_platform_PRD.md</c> D8, I12).
/// </summary>
public sealed class SparkResourceServerOptions
{
    /// <summary>The issuer's base URL; its discovery document supplies the signing keys and the introspection endpoint.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// The audience this application answers to: for a Spark identity provider, the name of the API resource.
    /// Required, for the reason <see cref="Extensions.SparkJwtBearerOptions.Audience"/> gives.
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Require HTTPS for the discovery document. Off only against a local http issuer.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Accept only tokens typed <c>at+jwt</c> (RFC 9068 §2.1), so an id token, which is also a signed JWT from the
    /// same issuer, is never taken for an access token. On by default; off only for an issuer that does not type
    /// its access tokens.
    /// </summary>
    public bool RequireAccessTokenType { get; set; } = true;

    /// <summary>
    /// Validate tokens by asking the issuer (RFC 7662) instead of checking the JWT locally. Slower, but a revoked
    /// token stops working at once rather than when it expires. Needs <see cref="IntrospectionClientId"/> and
    /// <see cref="IntrospectionClientSecret"/>: the resource server's own client at the issuer.
    /// </summary>
    public bool UseIntrospection { get; set; }

    public string? IntrospectionClientId { get; set; }

    public string? IntrospectionClientSecret { get; set; }

    /// <summary>How long an active introspection answer is reused, at most until the token expires.</summary>
    public TimeSpan IntrospectionCacheDuration { get; set; } = TimeSpan.FromSeconds(30);
}
