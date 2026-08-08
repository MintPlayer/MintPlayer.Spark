namespace MintPlayer.Spark.IdentityProvider.Configuration;

public class SparkIdentityProviderOptions
{
    /// <summary>
    /// The issuer identifier this provider stamps on every token it mints and requires on
    /// every token it validates — e.g. <c>https://id.example.com</c>.
    /// <para>
    /// Required outside Development. It was previously derived from the request's
    /// <c>Host</c> header on every issuance and validation path, which the client controls: a
    /// forged <c>Host</c> minted tokens claiming a different issuer, signed with the real key,
    /// and a relying party that trusts this key would accept them. In Development it still
    /// falls back to the request so a local run needs no configuration.
    /// </para>
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// Path to signing key file. Default: App_Data/oidc-signing-key.json
    /// Auto-generated in Development; must be provided in Production.
    /// </summary>
    public string SigningKeyPath { get; set; } = "App_Data/oidc-signing-key.json";

    /// <summary>
    /// Whether to auto-approve consent for clients with ConsentType = "implicit".
    /// </summary>
    public bool AutoApproveImplicitConsent { get; set; } = true;

    /// <summary>
    /// Token cleanup interval. Default: 1 hour.
    /// </summary>
    public TimeSpan TokenCleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// If true, automatically allows origins registered in
    /// OidcApplication.AllowedCorsOrigins for the OIDC endpoints.
    /// </summary>
    public bool EnableDynamicCors { get; set; } = true;
}
