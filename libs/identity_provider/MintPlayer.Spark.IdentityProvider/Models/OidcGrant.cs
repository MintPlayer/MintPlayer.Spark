namespace MintPlayer.Spark.IdentityProvider.Models;

/// <summary>
/// A user's consent to one application: the <c>OidcGrants</c> collection
/// (<c>docs/identity_provider_platform_PRD.md</c> D1, D6), one document per user × application with a
/// natural id (<c>OidcGrantReference</c>). Replaces <c>OidcAuthorizations</c>.
/// </summary>
public class OidcGrant
{
    public string? Id { get; set; }
    public string ApplicationId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    /// <summary><c>valid</c> while the grant is live, <c>revoked</c> once the user withdrew it.</summary>
    public string Status { get; set; } = "valid";
    /// <summary>The scopes the user granted. Narrowed by per-scope withdrawal; widened only by a new consent.</summary>
    public List<string> GrantedScopes { get; set; } = [];
    /// <summary>When the grant was first created.</summary>
    public DateTime CreatedAt { get; set; }
    /// <summary>When the user last consented, which is what <see cref="ExpiresAt"/> counts from.</summary>
    public DateTime? ConsentedAt { get; set; }
    /// <summary>
    /// Whether the user ticked "remember": the consent screen is skipped for the granted scopes
    /// until <see cref="ExpiresAt"/>. Without it, the grant still backs the tokens issued under it,
    /// but the next sign-in asks again.
    /// </summary>
    public bool Remembered { get; set; }
    /// <summary>When a remembered consent stops skipping the consent screen (the application's <c>ConsentLifetimeSeconds</c>); null for never.</summary>
    public DateTime? ExpiresAt { get; set; }
    /// <summary>The last time a token was issued under this grant, recorded at most once an hour.</summary>
    public DateTime? LastUsedAt { get; set; }
    /// <summary>When the current revocation happened, or null while the grant is live.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// When this grant was last withdrawn, <b>ever</b>. Unlike <see cref="RevokedAt"/> this is
    /// never cleared, because re-consenting must not resurrect tokens issued before the
    /// withdrawal — and clearing it would destroy the only evidence that they predate it.
    /// <para>
    /// The token sweep at withdrawal is best-effort (it rides an eventually-consistent index).
    /// This is what makes that acceptable: a token the sweep missed is still refused, because the
    /// decision compares its creation time against this rather than trusting its own status.
    /// Narrowing the grant to fewer scopes (per-scope withdrawal) sets it too, so a token carrying
    /// a withdrawn scope dies with the narrowing.
    /// </para>
    /// </summary>
    public DateTime? LastRevokedAt { get; set; }

    /// <summary>Whether a remembered consent still skips the consent screen at <paramref name="now"/>.</summary>
    public bool RemembersConsentAt(DateTime now)
        => Status == "valid" && Remembered && (ExpiresAt is null || ExpiresAt > now);
}
