using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace MintPlayer.Spark.WebPush;

/// <summary>
/// RFC 8291 message encryption with the RFC 8188 <c>aes128gcm</c> content coding: one record, a fresh
/// salt and a fresh ephemeral P-256 key per message, the record size 4096, the ephemeral public key as
/// <c>keyid</c>. Built on <see cref="ECDiffieHellman"/>, <see cref="HKDF"/> and <see cref="AesGcm"/> only.
/// </summary>
/// <remarks>Verified byte for byte against RFC 8291 Appendix A (<c>WebPushEncryptionTests</c>).</remarks>
public static class WebPushEncryption
{
    /// <summary>salt (16) ‖ rs (4) ‖ idlen (1) ‖ keyid (65).</summary>
    public const int HeaderLength = 16 + 4 + 1 + 65;

    private const int RecordSize = 4096;
    private const int TagLength = 16;
    private static readonly byte[] KeyInfoPrefix = Encoding.ASCII.GetBytes("WebPush: info\0");
    private static readonly byte[] CekInfo = Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0");
    private static readonly byte[] NonceInfo = Encoding.ASCII.GetBytes("Content-Encoding: nonce\0");

    /// <summary>Encrypts <paramref name="plaintext"/> for <paramref name="subscription"/>: the complete request body.</summary>
    /// <exception cref="ArgumentException">The subscription keys are malformed, or the plaintext exceeds <see cref="WebPushMessage.MaxPayloadBytes"/>.</exception>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, WebPushSubscription subscription)
    {
        Span<byte> salt = stackalloc byte[16];
        RandomNumberGenerator.Fill(salt);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(plaintext, subscription, salt, ephemeral);
    }

    /// <summary>The deterministic core, with the salt and the application server's key given (the RFC vectors).</summary>
    internal static byte[] Encrypt(ReadOnlySpan<byte> plaintext, WebPushSubscription subscription, ReadOnlySpan<byte> salt, ECDiffieHellman applicationServerKey)
    {
        if (plaintext.Length > WebPushMessage.MaxPayloadBytes)
            throw new ArgumentException($"A push payload is at most {WebPushMessage.MaxPayloadBytes} bytes; this one is {plaintext.Length}.", nameof(plaintext));
        if (salt.Length != 16)
            throw new ArgumentException("The salt is 16 bytes.", nameof(salt));

        var userAgentPublic = DecodeKey(subscription.P256dh, 65, "p256dh");
        var authSecret = DecodeKey(subscription.Auth, 16, "auth");
        if (userAgentPublic[0] != 0x04)
            throw new ArgumentException("The p256dh key is not an uncompressed P-256 point.", nameof(subscription));

        using var userAgentKey = ImportPublicKey(userAgentPublic);
        var applicationServerPublic = ExportPublicKey(applicationServerKey);

        // RFC 8291 §3.3: ecdh_secret, then IKM = HKDF(auth_secret, ecdh_secret, "WebPush: info" ‖ 0 ‖ ua_public ‖ as_public, 32).
        var ecdhSecret = applicationServerKey.DeriveRawSecretAgreement(userAgentKey.PublicKey);
        var keyInfo = Concat(KeyInfoPrefix, userAgentPublic, applicationServerPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);

        // RFC 8188 §2.2/2.3: PRK = HKDF-Extract(salt, IKM); CEK and NONCE expanded from it.
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt.ToArray());
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, CekInfo);
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, NonceInfo);

        var body = new byte[HeaderLength + plaintext.Length + 1 + TagLength];
        salt.CopyTo(body);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16, 4), RecordSize);
        body[20] = (byte)applicationServerPublic.Length;
        applicationServerPublic.CopyTo(body.AsSpan(21));

        // One record, the last: the plaintext followed by the 0x02 delimiter and no further padding.
        var padded = new byte[plaintext.Length + 1];
        plaintext.CopyTo(padded);
        padded[^1] = 0x02;

        using var aes = new AesGcm(cek, TagLength);
        aes.Encrypt(nonce, padded, body.AsSpan(HeaderLength, padded.Length), body.AsSpan(HeaderLength + padded.Length, TagLength));
        return body;
    }

    internal static byte[] DecodeKey(string? value, int length, string name)
    {
        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars((value ?? "").Trim().TrimEnd('='));
        }
        catch (FormatException)
        {
            throw new ArgumentException($"The {name} key is not base64url.", nameof(value));
        }
        if (bytes.Length != length)
            throw new ArgumentException($"The {name} key is {bytes.Length} bytes; {length} expected.", nameof(value));
        return bytes;
    }

    internal static ECDiffieHellman ImportPublicKey(byte[] uncompressed)
    {
        try
        {
            return ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = uncompressed[1..33], Y = uncompressed[33..65] },
            });
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            throw new ArgumentException("The p256dh key is not a point on P-256.");
        }
    }

    internal static byte[] ExportPublicKey(ECAlgorithm key)
    {
        var q = key.ExportParameters(false).Q;
        return Concat([0x04], q.X!, q.Y!);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}
