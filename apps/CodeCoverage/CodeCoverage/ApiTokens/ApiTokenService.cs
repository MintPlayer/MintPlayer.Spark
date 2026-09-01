using System.Security.Cryptography;
using System.Text;
using CodeCoverage.Entities;

namespace CodeCoverage.ApiTokens;

/// <summary>
/// Token format: "covt_" + 256-bit urlsafe random. Only the SHA-256 hex hash is
/// ever stored (as the document id), so a database leak leaks no credentials
/// and uniqueness holds by construction.
/// </summary>
public static class ApiTokenService
{
    public const string Prefix = "covt_";

    public static string GenerateTokenValue()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var value = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return Prefix + value;
    }

    public static string Hash(string tokenValue)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tokenValue)));

    public static bool LooksLikeToken(string? value)
        => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal) && value.Length > Prefix.Length + 20;
}
