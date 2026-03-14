namespace SparkId.Entities;

public class OidcApplication
{
    public string? Id { get; set; }
    public string ClientId { get; set; } = "";
    public string? ClientSecretHash { get; set; }
    public string DisplayName { get; set; } = "";
    public string ClientType { get; set; } = "confidential"; // "public" or "confidential"
    public string ConsentType { get; set; } = "explicit";    // "explicit" or "implicit"
    public List<string> RedirectUris { get; set; } = [];
    public List<string> PostLogoutRedirectUris { get; set; } = [];
    public List<string> AllowedScopes { get; set; } = [];
    public bool RequirePkce { get; set; } = true;
    public int AccessTokenLifetimeMinutes { get; set; } = 60;
    public int RefreshTokenLifetimeDays { get; set; } = 14;
    public bool Enabled { get; set; } = true;
}
