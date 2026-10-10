namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// Whether an account is the one an email address belongs to: the rule shared by every invitation that
/// is addressed to a mailbox, so the identity provider's team invitations
/// (<c>OidcInvitations.AcceptAsync</c>) and the organizations package's invitations
/// (<c>docs/seo_analyzer_PRD.md</c> D43) cannot drift apart.
/// </summary>
public static class SparkEmailOwnership
{
    /// <summary>The way ASP.NET Identity normalizes an address (<c>UpperInvariantLookupNormalizer</c>), trimmed.</summary>
    public static string Normalize(string? email) => (email ?? "").Trim().ToUpperInvariant();

    /// <summary>
    /// Whether <paramref name="email"/> is the account's address, compared on the account's stored
    /// <see cref="SparkUser.NormalizedEmail"/>. An account without an address matches nothing.
    /// </summary>
    public static bool IsAddressOf(string? accountNormalizedEmail, string? email)
        => accountNormalizedEmail is { Length: > 0 } && string.Equals(accountNormalizedEmail, Normalize(email), StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="user"/> owns <paramref name="email"/>: it is the account's address
    /// (<see cref="IsAddressOf"/>) and the account has confirmed it. An address an external provider did
    /// not vouch for, or one typed at registration and never confirmed, proves nothing.
    /// </summary>
    public static bool Owns(SparkUser user, string? email)
        => user.EmailConfirmed && IsAddressOf(user.NormalizedEmail, email);
}
