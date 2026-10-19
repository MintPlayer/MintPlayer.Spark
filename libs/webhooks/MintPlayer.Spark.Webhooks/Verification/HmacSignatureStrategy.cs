using System.Security.Cryptography;
using System.Text;

namespace MintPlayer.Spark.Webhooks;

/// <summary>The HMAC hash function a provider signs with.</summary>
public enum WebhookHmacAlgorithm
{
    /// <summary>HMAC-SHA1. Only for providers that still sign with it.</summary>
    Sha1,
    /// <summary>HMAC-SHA256: GitHub, Stripe, Shopify, most others.</summary>
    Sha256,
    /// <summary>HMAC-SHA384.</summary>
    Sha384,
    /// <summary>HMAC-SHA512.</summary>
    Sha512,
}

/// <summary>How the signature bytes are written into the header.</summary>
public enum WebhookSignatureEncoding
{
    /// <summary>Lower-case hexadecimal (GitHub, Stripe).</summary>
    Hex,
    /// <summary>Standard base64 with padding (Shopify).</summary>
    Base64,
}

/// <summary>
/// The shape of an "HMAC of the body in one header" signature.
/// </summary>
/// <param name="Header">The header that carries the signature, e.g. <c>X-Hub-Signature-256</c>.</param>
/// <param name="Algorithm">The HMAC hash function.</param>
/// <param name="Encoding">How the signature bytes are written.</param>
/// <param name="Prefix">Text before the encoded signature, e.g. <c>sha256=</c>; empty for none.</param>
public sealed record HmacSignatureScheme(
    string Header,
    WebhookHmacAlgorithm Algorithm = WebhookHmacAlgorithm.Sha256,
    WebhookSignatureEncoding Encoding = WebhookSignatureEncoding.Hex,
    string Prefix = "");

/// <summary>
/// Verifies an HMAC of the exact body bytes, written into one header as
/// <c>{Prefix}{encoded HMAC}</c>. Fail-closed on an empty secret, compared in constant time.
/// </summary>
/// <remarks>
/// This scheme carries no timestamp, so it cannot refuse a replay by itself: a captured delivery
/// verifies for as long as the secret is unchanged. Deduplication on the provider's delivery id
/// (<see cref="WebhookSourceBuilder.DeliveryId"/>) is what keeps a replay from being handled twice.
/// </remarks>
public sealed class HmacSignatureStrategy : IWebhookSignatureStrategy
{
    private readonly HmacSignatureScheme scheme;
    private readonly string? secret;

    /// <summary>A strategy for <paramref name="scheme"/>, keyed with <paramref name="secret"/> (UTF-8).</summary>
    public HmacSignatureStrategy(HmacSignatureScheme scheme, string? secret)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme.Header);
        this.scheme = scheme;
        this.secret = secret;
    }

    /// <inheritdoc />
    public WebhookVerification Verify(WebhookRequest request)
        => VerifyHeader(request.Header(scheme.Header), request.Body);

    /// <summary>
    /// Verifies a signature header value against body bytes. For callers that hold the two
    /// separately, such as a dev tunnel that relays the header and the body in an envelope.
    /// </summary>
    public WebhookVerification VerifyHeader(string? presented, ReadOnlySpan<byte> body)
    {
        // Fail-closed when no secret is configured: an empty secret cannot prove anything about the
        // body's origin, so accepting unsigned deliveries would let any unauthenticated POST through.
        if (string.IsNullOrEmpty(secret))
            return WebhookVerification.Rejected(WebhookRejectionReason.NoSecretConfigured);
        if (string.IsNullOrEmpty(presented))
            return WebhookVerification.Rejected(WebhookRejectionReason.MissingSignature);

        var expected = scheme.Prefix + Encode(Hash(scheme.Algorithm, Encoding.UTF8.GetBytes(secret), body), scheme.Encoding);

        // Constant-time comparison: string.Equals stops at the first mismatched byte and leaks
        // per-byte timing, which lets a remote attacker recover a valid HMAC one byte at a time.
        return FixedTimeEquals(presented, expected)
            ? WebhookVerification.Signed()
            : WebhookVerification.Rejected(WebhookRejectionReason.InvalidSignature);
    }

    internal static byte[] Hash(WebhookHmacAlgorithm algorithm, byte[] key, ReadOnlySpan<byte> data) => algorithm switch
    {
        WebhookHmacAlgorithm.Sha1 => HMACSHA1.HashData(key, data),
        WebhookHmacAlgorithm.Sha256 => HMACSHA256.HashData(key, data),
        WebhookHmacAlgorithm.Sha384 => HMACSHA384.HashData(key, data),
        WebhookHmacAlgorithm.Sha512 => HMACSHA512.HashData(key, data),
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, null),
    };

    internal static string Encode(byte[] hash, WebhookSignatureEncoding encoding) => encoding switch
    {
        WebhookSignatureEncoding.Hex => Convert.ToHexStringLower(hash),
        WebhookSignatureEncoding.Base64 => Convert.ToBase64String(hash),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null),
    };

    internal static bool FixedTimeEquals(string presented, string expected)
    {
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        // The length of the expected value is public (it is fixed by the scheme), so an early exit on
        // a length mismatch leaks nothing an attacker does not already know.
        return presentedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes);
    }
}
