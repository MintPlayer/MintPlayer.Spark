namespace MintPlayer.Spark.IdentityProvider.Models;

public class OidcAuthorization
{
    public string? Id { get; set; }
    public string ApplicationId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Status { get; set; } = "valid";
    public List<string> GrantedScopes { get; set; } = [];
    public DateTime CreatedAt { get; set; }
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
    /// </para>
    /// </summary>
    public DateTime? LastRevokedAt { get; set; }
}
