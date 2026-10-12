namespace MintPlayer.Spark.IdentityProvider.Models;

/// <summary>
/// One refresh-token family: the <c>OidcRefreshFamilies</c> collection (<c>docs/spark_mcp_PRD.md</c> decision log,
/// "Refresh-token families exact"). A family is the refresh tokens rotated from one redemption of an authorization code
/// or device code, and the access tokens issued with them; each of them carries the family's id
/// (<see cref="OidcToken.FamilyId"/>).
/// <para>
/// The family is the decision, loaded by id: a refresh token is honoured only while its family is <c>valid</c> and the
/// token is the family's current <see cref="Generation"/>, and an access token of the family only while the family is
/// <c>valid</c>. A rotated refresh token presented again, one refresh token or code spent twice at once, or a code
/// presented twice revokes the family (RFC 9700 §4.14.2, RFC 6749 §4.1.2), so its active refresh token and its access
/// tokens are refused from the next request on. The sweep that also marks those tokens revoked rides an index and may
/// miss one issued moments earlier; it is cleanup, and nothing depends on it.
/// </para>
/// <para>
/// Carries <c>@expires</c> at <see cref="ExpiresAt"/>, the latest expiry of any token in it, so it outlives every token it
/// decides for.
/// </para>
/// </summary>
public class OidcRefreshFamily
{
    /// <summary>
    /// <c>OidcRefreshFamilies/&lt;hash&gt;</c>, derived from the id of the code or device code that started it, so a code
    /// presented again finds the family it started.
    /// </summary>
    public string? Id { get; set; }
    public string ApplicationId { get; set; } = string.Empty;
    /// <summary>
    /// The <see cref="OidcGrant"/> the family was issued under. Withdrawing the grant ends the family through it: every
    /// use of a token loads the grant beside the family.
    /// </summary>
    public string AuthorizationId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    /// <summary><c>valid</c>, or <c>revoked</c> once a theft signal ended it (<see cref="RevokedReason"/>).</summary>
    public string Status { get; set; } = "valid";
    /// <summary>The generation of the active refresh token: 1 when the family starts, one more at every rotation.</summary>
    public int Generation { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>When the last rotation issued the active refresh token; null before the first.</summary>
    public DateTime? RotatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    /// <summary>What revoked the family (<see cref="OidcRefreshFamilyRevocations"/>); null while it stands.</summary>
    public string? RevokedReason { get; set; }
    /// <summary>The latest expiry of any token in the family.</summary>
    public DateTime ExpiresAt { get; set; }
}

/// <summary>The values of <see cref="OidcRefreshFamily.RevokedReason"/>.</summary>
public static class OidcRefreshFamilyRevocations
{
    /// <summary>A rotated refresh token was presented again, or one refresh token was spent twice at once (RFC 9700 §4.14.2).</summary>
    public const string RefreshTokenReuse = "refresh_token_reuse";

    /// <summary>The code that started the family was presented again, or spent twice at once (RFC 6749 §4.1.2).</summary>
    public const string CodeReplay = "code_replay";
}
