using System.Security.Cryptography;
using System.Text;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// The id of the <see cref="Models.OidcAuthorization"/> recording what a user has granted an
/// application. Derived from the pair, so a user holds exactly one authorization per
/// application by construction.
/// <para>
/// Unlike <see cref="OpaqueHandle"/> this is not a secret — the hash is here to fold two
/// arbitrary strings into one well-formed id. What it buys is consistency: the previous
/// lookup went through an eventually-consistent index, which made two security decisions read
/// stale state. A consent revoked moments earlier could still satisfy the "already consented"
/// check and skip the consent screen entirely, and concurrent authorize requests could each
/// miss the other's write and create rival grant records — splitting the token chain that
/// revocation sweeps by <c>AuthorizationId</c>.
/// </para>
/// </summary>
internal static class OidcAuthorizationReference
{
    private const string CollectionPrefix = "OidcAuthorizations/";

    public static string DocumentId(string subject, string applicationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentException.ThrowIfNullOrEmpty(applicationId);

        // Length-prefixed so the pair maps into the hash injectively. A bare separator would
        // not: under "a|b", the pairs ("x|y", "z") and ("x", "y|z") hash identically — one
        // user's grant answering for another's.
        var key = Encoding.UTF8.GetBytes($"{subject.Length}:{subject}|{applicationId}");
        return CollectionPrefix + Convert.ToHexStringLower(SHA256.HashData(key));
    }
}
