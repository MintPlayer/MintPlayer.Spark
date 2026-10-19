using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Secrets;

/// <summary>
/// The ticket secret (§4.11, default 9, S8): 128 random bits, verified through PBKDF2-HMAC-SHA256 with a per-ticket
/// 16-byte salt, 50,000 iterations by default. PBKDF2 is native in .NET (<see cref="Rfc2898DeriveBytes.Pbkdf2(byte[], byte[], int, HashAlgorithmName, int)"/>)
/// and in browser WebCrypto (<c>crypto.subtle.deriveBits({ name: 'PBKDF2', hash: 'SHA-256', … })</c>), so the offline
/// scanner verifies with the same inputs and no library (K11). Pure, except for the random draws.
/// </summary>
/// <remarks>
/// <para><b>What PBKDF2 hashes.</b> The UTF-8 bytes of the secret <em>as printed</em> (22 base64url characters), so the
/// browser hashes exactly the text it read from the QR code.</para>
/// <para><b>The QR payload.</b> <c>{prefix}{code}.{secret}</c> with the prefix <c>ST1.</c> (version 1): the code is the
/// lookup key, the secret proves possession. <see cref="TryParse"/> accepts the payload with or without the prefix,
/// trimmed, case-sensitive for the secret.</para>
/// </remarks>
public static class TicketSecrets
{
    public const string QrPrefix = "ST1.";

    public const int SecretBytes = 16;

    public const int SaltBytes = 16;

    public const int HashBytes = 32;

    private const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base 32

    /// <summary>A new ticket code: 16 Crockford base-32 characters (80 random bits).</summary>
    public static string NewCode()
    {
        Span<byte> random = stackalloc byte[16];
        RandomNumberGenerator.Fill(random);
        var chars = new char[16];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = CodeAlphabet[random[i] & 31];
        return new string(chars);
    }

    /// <summary>Whether <paramref name="code"/> has the shape of a ticket code.</summary>
    public static bool IsCode(string? code)
        => code is { Length: 16 } && code.All(c => CodeAlphabet.Contains(c));

    /// <summary>A new secret: 128 random bits as 22 base64url characters.</summary>
    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>The verifier of <paramref name="secret"/>: a new salt and its PBKDF2 output.</summary>
    public static TicketSecretVerifier Hash(string secret, int iterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return new TicketSecretVerifier(Base64Url(salt), Base64Url(Derive(secret, salt, iterations)), iterations);
    }

    /// <summary>Whether <paramref name="secret"/> matches the ticket's verifier (constant time over the hash).</summary>
    public static bool Verify(Ticket ticket, string? secret)
        => Verify(new TicketSecretVerifier(ticket.SecretSalt, ticket.SecretHash, ticket.SecretIterations), secret);

    /// <inheritdoc cref="Verify(Ticket, string?)"/>
    public static bool Verify(TicketSecretVerifier verifier, string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length > 64 || verifier.Iterations < 1
            || !TryFromBase64Url(verifier.Salt, out var salt) || !TryFromBase64Url(verifier.Hash, out var expected))
            return false;
        var actual = Derive(secret, salt, verifier.Iterations);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>The QR payload of a ticket.</summary>
    public static string Payload(string code, string secret) => QrPrefix + code + "." + secret;

    /// <summary>Splits a scanned payload into code and secret; false for anything else.</summary>
    public static bool TryParse(string? payload, out string code, out string secret)
    {
        code = secret = "";
        if (string.IsNullOrWhiteSpace(payload))
            return false;
        var text = payload.Trim();
        if (text.StartsWith(QrPrefix, StringComparison.OrdinalIgnoreCase))
            text = text[QrPrefix.Length..];
        var dot = text.IndexOf('.');
        if (dot <= 0 || dot == text.Length - 1)
            return false;
        var candidate = text[..dot].ToUpperInvariant();
        var rest = text[(dot + 1)..];
        if (!IsCode(candidate) || rest.Length is < 16 or > 64 || !rest.All(IsBase64UrlChar))
            return false;
        code = candidate;
        secret = rest;
        return true;
    }

    /// <summary>PBKDF2-HMAC-SHA256 over the UTF-8 bytes of <paramref name="secret"/>.</summary>
    public static byte[] Derive(string secret, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, iterations, HashAlgorithmName.SHA256, HashBytes);

    /// <summary>SHA-256 hex of a high-entropy token (device tokens, transfer tokens): no stretching needed.</summary>
    public static string TokenHash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>A new high-entropy token (256 bits, base64url).</summary>
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Whether <paramref name="token"/> matches <paramref name="hash"/> (constant time).</summary>
    public static bool TokenMatches(string? token, string? hash)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(hash) || token.Length > 128)
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(TokenHash(token)), Encoding.ASCII.GetBytes(hash));
    }

    public static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryFromBase64Url(string? text, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(text))
            return false;
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsBase64UrlChar(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
}

/// <summary>A secret's PBKDF2 verifier: salt and hash (base64url) and the iteration count.</summary>
public sealed record TicketSecretVerifier(string Salt, string Hash, int Iterations);

/// <summary>
/// Keeps the secret recoverable for its holder's ticket page and PDF only, encrypted with ASP.NET Core Data Protection
/// (purpose <c>MintPlayer.Spark.Commerce.Ticketing.Tickets.Secret.v1</c>). The verifier on the ticket is what the door
/// checks; this copy is never listed or exported.
/// </summary>
public sealed class TicketSecretVault(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector("MintPlayer.Spark.Commerce.Ticketing.Tickets.Secret.v1");

    public TicketSecret Protect(string code, string secret, int generation)
        => new() { Id = TicketSecret.IdFor(code), Protected = protector.Protect(code + ":" + generation + ":" + secret), Generation = generation };

    /// <summary>The secret of <paramref name="code"/>'s current <paramref name="generation"/>, or null when the stored copy is of another one or cannot be read.</summary>
    public string? Unprotect(TicketSecret? stored, string code, int generation)
    {
        if (stored is null || stored.Generation != generation)
            return null;
        try
        {
            var parts = protector.Unprotect(stored.Protected).Split(':', 3);
            return parts.Length == 3 && parts[0] == code && parts[1] == generation.ToString(System.Globalization.CultureInfo.InvariantCulture) ? parts[2] : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
