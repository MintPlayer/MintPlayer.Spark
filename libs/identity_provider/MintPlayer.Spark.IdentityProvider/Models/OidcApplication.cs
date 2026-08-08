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
    /// <summary>
    /// The grants this client may use. **Must not carry a non-empty initializer.**
    /// <para>
    /// RavenDB's serializer populates the collection the property initializer already created
    /// rather than replacing it, so a default of <c>["authorization_code"]</c> was silently
    /// re-added to every application on load. A client stored as <c>client_credentials</c>-only
    /// came back as <c>["authorization_code", "client_credentials"]</c> — which defeats the
    /// grant gating outright: the interactive flow accepted a machine client no matter what was
    /// configured, and the check that was supposed to stop it could never fail.
    /// </para>
    /// <para>
    /// Empty now means empty, so a client that declares no grants can use none — fail closed.
    /// </para>
    /// </summary>
    public List<string> AllowedGrantTypes { get; set; } = [];

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
