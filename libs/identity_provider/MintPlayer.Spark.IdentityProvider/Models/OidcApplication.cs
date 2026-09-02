namespace MintPlayer.Spark.IdentityProvider.Models;

/// <summary>
/// Represents an OIDC client application registered with the identity provider.
/// This model is used internally by the IdentityProvider endpoints to read
/// OidcApplication documents from RavenDB.
/// </summary>
public class OidcApplication
{
    /// <summary>Unique identifier of the client application, assigned automatically on creation.</summary>
    public string? Id { get; set; }

    // --- Identity ---
    /// <summary>Public client identifier the application sends in every OIDC request; must be unique.</summary>
    public string ClientId { get; set; } = string.Empty;
    /// <summary>Human-readable name of the application, shown to users on the consent screen.</summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Either <c>confidential</c> for a server that can keep a secret, or <c>public</c> for a browser or mobile app that cannot.</summary>
    public string ClientType { get; set; } = "confidential"; // "public" or "confidential"
    /// <summary>Whether the application may currently sign users in; disable to block it without deleting it.</summary>
    public bool Enabled { get; set; } = true;

    // --- Secrets (supports rotation: multiple secrets with expiration) ---
    /// <summary>Secrets a confidential client authenticates with; several can coexist so a secret can be rotated.</summary>
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

    /// <summary>
    /// Lets this client introspect tokens it neither issued nor is the audience of.
    /// <para>
    /// Off by default: introspection discloses a token's subject and scopes, so the caller must
    /// have a reason to see them — it issued the token, or the token was minted for it. Turn this
    /// on only for a gateway that introspects on behalf of the resources behind it, and
    /// understand that it can then read every token in the system.
    /// </para>
    /// </summary>
    public bool MayIntrospectAnyAudience { get; set; }

    // --- URIs ---
    /// <summary>Exact URIs the identity provider may redirect back to after sign-in; any other URI is rejected.</summary>
    public List<string> RedirectUris { get; set; } = [];
    /// <summary>Exact URIs the identity provider may redirect back to after the user signs out.</summary>
    public List<string> PostLogoutRedirectUris { get; set; } = [];
    /// <summary>Browser origins allowed to call the OIDC endpoints cross-site, e.g. <c>https://app.example.com</c>.</summary>
    public List<string> AllowedCorsOrigins { get; set; } = [];

    // --- Scopes & Claims ---
    /// <summary>Names of the scopes this application may request; a request for any other scope is refused.</summary>
    public List<string> AllowedScopes { get; set; } = [];
    /// <summary>Fixed claims added to every token issued to this application.</summary>
    public List<ClientClaim> Claims { get; set; } = [];

    // --- Consent ---
    /// <summary>Either <c>explicit</c> to ask the user for consent on the consent screen, or <c>implicit</c> to grant it automatically.</summary>
    public string ConsentType { get; set; } = "explicit"; // "explicit" or "implicit"
    /// <summary>Whether the user may tick a box so the consent screen is skipped on later sign-ins.</summary>
    public bool AllowRememberConsent { get; set; } = true;
    /// <summary>How long, in seconds, a remembered consent stays valid; leave empty to keep it until withdrawn.</summary>
    public int? ConsentLifetimeSeconds { get; set; }

    // --- Token lifetimes ---
    /// <summary>Whether the authorization code flow must use PKCE; keep enabled unless a legacy client cannot support it.</summary>
    public bool RequirePkce { get; set; } = true;
    /// <summary>Validity of an issued access token in minutes.</summary>
    public int AccessTokenLifetimeMinutes { get; set; } = 60;
    /// <summary>Validity of an issued refresh token in days.</summary>
    public int RefreshTokenLifetimeDays { get; set; } = 14;
}

public class ClientSecret
{
    /// <summary>The client secret; a plain value entered here is hashed on save and cannot be read back afterwards.</summary>
    public string Hash { get; set; } = string.Empty;
    /// <summary>Optional note telling secrets apart, e.g. <c>Production 2026</c>.</summary>
    public string? Description { get; set; }
    /// <summary>Moment the secret was created, in UTC.</summary>
    public DateTime CreatedAt { get; set; }
    /// <summary>Moment after which the secret is no longer accepted, in UTC; leave empty for a secret that never expires.</summary>
    public DateTime? ExpiresAt { get; set; }
}

public class ClientClaim
{
    /// <summary>Claim type added to issued tokens, e.g. <c>tenant</c>.</summary>
    public string Type { get; set; } = string.Empty;
    /// <summary>Value issued for the claim type.</summary>
    public string Value { get; set; } = string.Empty;
}
