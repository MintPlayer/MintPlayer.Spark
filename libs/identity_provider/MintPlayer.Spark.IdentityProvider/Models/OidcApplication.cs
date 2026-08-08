namespace MintPlayer.Spark.IdentityProvider.Models;

/// <summary>
/// Represents an OIDC client application registered with the identity provider.
/// This model is used internally by the IdentityProvider endpoints to read
/// OidcApplication documents from RavenDB.
/// </summary>
public class OidcApplication
{
    public string? Id { get; set; }

    // --- Identity ---
    public string ClientId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ClientType { get; set; } = "confidential"; // "public" or "confidential"
    public bool Enabled { get; set; } = true;

    // --- Secrets (supports rotation: multiple secrets with expiration) ---
    public List<ClientSecret> Secrets { get; set; } = [];

    // --- Grant types ---
    public List<string> AllowedGrantTypes { get; set; } = ["authorization_code"];

    // --- URIs ---
    public List<string> RedirectUris { get; set; } = [];
    public List<string> PostLogoutRedirectUris { get; set; } = [];
    public List<string> AllowedCorsOrigins { get; set; } = [];

    // --- Scopes & Claims ---
    public List<string> AllowedScopes { get; set; } = [];
    public List<ClientClaim> Claims { get; set; } = [];

    // --- Consent ---
    public string ConsentType { get; set; } = "explicit"; // "explicit" or "implicit"
    public bool AllowRememberConsent { get; set; } = true;
    public int? ConsentLifetimeSeconds { get; set; }

    // --- Token lifetimes ---
    public bool RequirePkce { get; set; } = true;
    public int AccessTokenLifetimeMinutes { get; set; } = 60;
    public int RefreshTokenLifetimeDays { get; set; } = 14;
}

public class ClientSecret
{
    public string Hash { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public class ClientClaim
{
    public string Type { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
