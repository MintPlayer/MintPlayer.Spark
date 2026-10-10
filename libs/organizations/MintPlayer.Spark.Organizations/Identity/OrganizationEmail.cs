using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Organizations.Identity;

/// <summary>
/// The email rules of invitations and join requests (<c>docs/seo_analyzer_PRD.md</c> D43, as corrected by
/// the owner on 2026-10-10). A mail sent on a user's behalf needs that user's address confirmed; the
/// invited address needs no account, so accepting only compares addresses.
/// </summary>
internal static class OrganizationEmail
{
    /// <summary>The way ASP.NET Identity normalizes an address (<c>UpperInvariantLookupNormalizer</c>), trimmed.</summary>
    public static string Normalize(string? email) => (email ?? "").Trim().ToUpperInvariant();

    /// <summary>
    /// Whether <paramref name="email"/> is <paramref name="account"/>'s address, compared on its stored
    /// <see cref="SparkUser.NormalizedEmail"/>. An account without an address matches nothing.
    /// </summary>
    public static bool IsAddressOf(SparkUser account, string? email)
        => account.NormalizedEmail is { Length: > 0 } stored && string.Equals(stored, Normalize(email), StringComparison.Ordinal);

    /// <summary>Whether mail may go out on <paramref name="account"/>'s behalf: it has an address and confirmed it.</summary>
    public static bool HasConfirmedAddress(SparkUser? account)
        => account is { EmailConfirmed: true, Email.Length: > 0 };
}
