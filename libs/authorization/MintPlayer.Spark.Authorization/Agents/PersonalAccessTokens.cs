using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace MintPlayer.Spark.Authorization.Agents;

/// <summary>
/// Personal access tokens (MCP PRD D11): <c>spat_{user}.{tokenId}.{secret}</c>, shown once, stored as a SHA-256
/// on the token's <see cref="AgentGrant"/>, valid 90 days by default and a year at most.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the user id is in the token.</b> The grant's natural id is <c>AgentGrants/{userId}/pat/{tokenId}</c>
/// (D12); carrying both lets the check load that one document by id, with no index (stale right after
/// creation) and no lookup collection. The user id is not a secret; the 256-bit <c>secret</c> is the credential.
/// </para>
/// <para>
/// A plain SHA-256 is enough for a value with 256 bits of entropy: there is nothing to guess, so a slow
/// password hash would only slow every call. Compared in constant time.
/// </para>
/// </remarks>
public static class PersonalAccessTokens
{
    /// <summary>Every token starts with it, so secret scanners and people recognise one.</summary>
    public const string Prefix = "spat_";

    /// <summary>The bearer scheme that validates tokens on <c>/mcp</c>. Not one of Spark's web credentials.</summary>
    public const string Scheme = "SparkPersonalAccessToken";

    /// <summary>The claim naming the grant on a token's principal.</summary>
    public const string GrantClaim = "spark_agent_grant";

    /// <summary>The lifetime a token gets when its creator picks none (D11).</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(90);

    /// <summary>The longest lifetime a token may have (D11).</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(365);

    private const int MaxLength = 512;

    /// <summary>A new token for <paramref name="userId"/>, and its token id (the grant's last id segment).</summary>
    internal static string Create(string userId, out string tokenId)
    {
        tokenId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return $"{Prefix}{WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(userId))}.{tokenId}.{secret}";
    }

    /// <summary>Splits a presented token; <see langword="false"/> for anything not shaped like one.</summary>
    internal static bool TryParse(string? token, out string userId, out string tokenId)
    {
        userId = tokenId = string.Empty;
        if (token is null || token.Length > MaxLength || !token.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var parts = token[Prefix.Length..].Split('.');
        if (parts.Length != 3 || parts[1].Length != 32 || parts[2].Length == 0 || !parts[1].All(char.IsAsciiHexDigitLower))
            return false;

        try
        {
            userId = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(parts[0]));
        }
        catch (FormatException)
        {
            return false;
        }

        tokenId = parts[1];
        return userId.Length > 0 && !userId.Any(char.IsControl);
    }

    /// <summary>What the grant stores: the token's SHA-256, hex.</summary>
    internal static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Whether <paramref name="token"/> hashes to <paramref name="hash"/>, in constant time.</summary>
    internal static bool Matches(string token, string? hash)
        => hash is { Length: 64 }
           && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(hash));
}
