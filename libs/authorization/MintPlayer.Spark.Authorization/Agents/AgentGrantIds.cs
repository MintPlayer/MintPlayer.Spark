using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.Authorization.Agents;

/// <summary>
/// The natural ids of <see cref="AgentGrant"/> (MCP PRD D12). Every id starts with the user's prefix, so the
/// account endpoints rebuild a grant's id from the signed-in user and the grant's <i>key</i> (what follows the
/// prefix): a user can name only their own grants, whatever key they send.
/// </summary>
public static partial class AgentGrantIds
{
    private const string Collection = "AgentGrants/";

    /// <summary><c>AgentGrants/{userId}/</c>: every grant of the user.</summary>
    public static string Prefix(string userId) => Collection + userId + "/";

    /// <summary><c>AgentGrants/{userId}/pat/{tokenId}</c>.</summary>
    public static string PersonalAccessToken(string userId, string tokenId) => Prefix(userId) + "pat/" + tokenId;

    /// <summary><c>AgentGrants/{userId}/ext/{sha256(issuer|clientId)[..32]}</c>: issuer-bound, so equal client ids of two servers never share a grant.</summary>
    public static string ExternalAuthorizationServer(string userId, string issuer, string clientId)
        => Prefix(userId) + "ext/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(issuer + "|" + clientId)))[..32];

    /// <summary><c>AgentGrants/{userId}/app/{clientId}</c>: a client of the embedded server.</summary>
    public static string Application(string userId, string clientId) => Prefix(userId) + "app/" + clientId;

    /// <summary>The key of a grant: its id without the user's prefix (<c>pat/…</c>, <c>ext/…</c>, <c>app/…</c>).</summary>
    public static string KeyOf(AgentGrant grant) => grant.Id![Prefix(grant.UserId).Length..];

    /// <summary>
    /// The id of the user's grant with <paramref name="key"/>, or <see langword="null"/> for a key no grant can
    /// have. Strict: a key is a kind and a hex id, or <c>app/</c> and a client id without control characters.
    /// </summary>
    public static string? FromKey(string userId, string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 600)
            return null;
        if (HexKey().IsMatch(key))
            return Prefix(userId) + key;
        if (key.StartsWith("app/", StringComparison.Ordinal) && key.Length > 4 && !key.Any(char.IsControl))
            return Prefix(userId) + key;
        return null;
    }

    [GeneratedRegex("^(pat|ext)/[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexKey();
}
