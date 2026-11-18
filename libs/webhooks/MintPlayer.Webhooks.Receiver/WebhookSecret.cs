using System;
using System.Security.Cryptography;

namespace MintPlayer.Webhooks;

/// <summary>
/// A symmetric Standard Webhooks signing secret: 24 to 64 random bytes, written as <c>whsec_</c> + base64.
/// The HMAC key is the base64-decoded part after the prefix.
/// </summary>
public sealed class WebhookSecret
{
    public const string Prefix = "whsec_";
    public const int MinBytes = 24;
    public const int MaxBytes = 64;
    public const int DefaultBytes = 32;

    private readonly byte[] key;

    private WebhookSecret(byte[] key) => this.key = key;

    /// <summary>The raw HMAC key. A copy, so a caller cannot change the secret.</summary>
    public byte[] GetKey() => (byte[])key.Clone();

    internal byte[] Key => key;

    /// <summary>A new random secret of <paramref name="bytes"/> bytes (default 32).</summary>
    public static WebhookSecret Generate(int bytes = DefaultBytes)
    {
        if (bytes < MinBytes || bytes > MaxBytes)
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, $"A webhook secret is {MinBytes} to {MaxBytes} bytes.");
        var key = new byte[bytes];
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(key);
        return new WebhookSecret(key);
    }

    /// <summary>Parses <c>whsec_&lt;base64&gt;</c>. The prefix is optional, as some producers omit it.</summary>
    public static WebhookSecret Parse(string value)
    {
        if (TryParse(value, out var secret, out var error))
            return secret!;
        throw new FormatException(error);
    }

    public static bool TryParse(string? value, out WebhookSecret? secret) => TryParse(value, out secret, out _);

    private static bool TryParse(string? value, out WebhookSecret? secret, out string error)
    {
        secret = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "A webhook secret is required.";
            return false;
        }

        var encoded = value!.Trim();
        if (encoded.StartsWith(Prefix, StringComparison.Ordinal))
            encoded = encoded.Substring(Prefix.Length);

        byte[] key;
        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            error = "A webhook secret is base64 after the whsec_ prefix.";
            return false;
        }

        if (key.Length < MinBytes || key.Length > MaxBytes)
        {
            error = $"A webhook secret is {MinBytes} to {MaxBytes} bytes; this one is {key.Length}.";
            return false;
        }

        secret = new WebhookSecret(key);
        error = string.Empty;
        return true;
    }

    /// <summary>The <c>whsec_</c> form. This is the secret itself: never log it.</summary>
    public override string ToString() => Prefix + Convert.ToBase64String(key);
}
