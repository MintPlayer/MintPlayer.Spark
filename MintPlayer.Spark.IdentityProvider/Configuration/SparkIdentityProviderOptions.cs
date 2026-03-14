namespace MintPlayer.Spark.IdentityProvider.Configuration;

public class SparkIdentityProviderOptions
{
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
}
