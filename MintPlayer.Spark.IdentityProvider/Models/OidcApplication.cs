namespace MintPlayer.Spark.IdentityProvider.Models;

/// <summary>
/// Represents an OIDC client application registered with the identity provider.
/// This model is used internally by the IdentityProvider endpoints to read
/// OidcApplication documents from RavenDB.
/// </summary>
public class OidcApplication
{
    public string? Id { get; set; }
    public string ClientId { get; set; } = "";
    public string? ClientSecretHash { get; set; }
    public string DisplayName { get; set; } = "";
    public string ClientType { get; set; } = "confidential";
    public string ConsentType { get; set; } = "explicit";
    public List<string> RedirectUris { get; set; } = [];
    public List<string> PostLogoutRedirectUris { get; set; } = [];
    public List<string> AllowedScopes { get; set; } = [];
    public bool RequirePkce { get; set; } = true;
    public int AccessTokenLifetimeMinutes { get; set; } = 60;
    public int RefreshTokenLifetimeDays { get; set; } = 14;
    public bool Enabled { get; set; } = true;
}
