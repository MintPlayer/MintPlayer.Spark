namespace MintPlayer.Spark.IdentityProvider.Models;

/// <summary>
/// A signing or encryption key of the provider: the <c>OidcKeys</c> collection
/// (<c>docs/identity_provider_platform_PRD.md</c> D1, D8). The private key material is protected with
/// ASP.NET Data Protection (purpose <c>Spark.IdentityProvider.OidcKeys</c>) and never stored in the
/// clear. Losing the Data Protection key ring makes it unreadable, and startup then fails rather than
/// silently minting new keys (spike S2).
/// </summary>
public class OidcKey
{
    /// <summary>The document id, <c>OidcKeys/&lt;kid&gt;</c>.</summary>
    public string? Id { get; set; }
    /// <summary>The key id published in the JWKS and stamped into every token header.</summary>
    public string Kid { get; set; } = string.Empty;
    /// <summary><c>sig</c> or <c>enc</c>.</summary>
    public string Use { get; set; } = "sig";
    /// <summary>The JOSE algorithm: <c>RS256</c>, <c>PS256</c>, <c>ES256</c>, or <c>RSA-OAEP</c> for encryption.</summary>
    public string Algorithm { get; set; } = "RS256";
    /// <summary><c>Next</c> (published, not yet signing), <c>Active</c> (signing), <c>Retired</c> (published until the tokens it signed expire).</summary>
    public string State { get; set; } = OidcKeyStates.Next;
    /// <summary>The private key as a JWK, protected with Data Protection.</summary>
    public string ProtectedPrivateJwk { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    /// <summary>When the key started signing.</summary>
    public DateTime? ActivatedAt { get; set; }
    /// <summary>When the key stopped signing.</summary>
    public DateTime? RetiredAt { get; set; }
}

public static class OidcKeyStates
{
    public const string Next = "Next";
    public const string Active = "Active";
    public const string Retired = "Retired";
}
