using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace MintPlayer.Spark.ReferenceData.Releases;

/// <summary>
/// Release signing (PRD §4.6, RD-DEF 5; spike RS17): Ed25519 (RFC 8032, pure) over the manifest's bytes, written beside
/// it as <c>manifest.sig</c>: one line <c>ed25519 {base64 signature}</c>. Keys are the raw 32-byte RFC 8032 seed
/// (private) and public key, in base64.
/// </summary>
/// <remarks>
/// <b>RS17 (desk, 2026-10-19).</b> .NET 11's BCL has no standalone Ed25519 (System.Security.Cryptography ships ML-DSA and
/// the composite ML-DSA-with-Ed25519 algorithms only; checked in the 11.0.0-rc.2 reference assembly). The implementation
/// is BouncyCastle.Cryptography (MIT licence, fully managed, so Windows and Linux sign and verify the same way, RS10),
/// rather than NSec (MIT, wraps the native libsodium). Round trip and tamper refusal: <c>ReleaseSigningTests</c>.
/// </remarks>
public static class ReleaseSignatures
{
    public const string Algorithm = "ed25519";

    public const int KeyLength = 32;

    /// <summary>A new private key (32 random bytes from the OS generator).</summary>
    public static byte[] NewPrivateKey() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(KeyLength);

    /// <summary>The public key of <paramref name="privateKey"/>.</summary>
    public static byte[] PublicKeyOf(ReadOnlySpan<byte> privateKey)
        => new Ed25519PrivateKeyParameters(Key(privateKey, "private"), 0).GeneratePublicKey().GetEncoded();

    /// <summary>The detached signature of <paramref name="data"/> (64 bytes).</summary>
    public static byte[] Sign(ReadOnlySpan<byte> privateKey, byte[] data)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(Key(privateKey, "private"), 0));
        signer.BlockUpdate(data, 0, data.Length);
        return signer.GenerateSignature();
    }

    /// <summary>Whether <paramref name="signature"/> is <paramref name="publicKey"/>'s signature of <paramref name="data"/>.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, byte[] data, byte[] signature)
    {
        if (signature.Length != 64) return false;
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(Key(publicKey, "public"), 0));
        verifier.BlockUpdate(data, 0, data.Length);
        return verifier.VerifySignature(signature);
    }

    /// <summary>The <c>manifest.sig</c> bytes for a signature.</summary>
    public static byte[] Format(byte[] signature) => Encoding.ASCII.GetBytes($"{Algorithm} {Convert.ToBase64String(signature)}\n");

    /// <summary>The signature in <c>manifest.sig</c> bytes, or <see cref="ReleaseRefusedException"/>.</summary>
    public static byte[] Parse(byte[] file)
    {
        var text = Encoding.ASCII.GetString(file).Trim();
        var parts = text.Split(' ', 2);
        if (parts.Length != 2 || parts[0] != Algorithm)
            throw new ReleaseRefusedException("manifest.sig is not an ed25519 signature.");
        try
        {
            return Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            throw new ReleaseRefusedException("manifest.sig is not valid base64.");
        }
    }

    /// <summary>
    /// The consumer's check (§4.7) for <see cref="ReleaseReader.ReadAsync"/>: with a public key configured, an unsigned
    /// release or a signature that does not verify is refused; without one, signatures are not checked.
    /// </summary>
    public static Action<byte[], byte[]?>? Verifier(byte[]? publicKey)
        => publicKey is null ? null : (manifest, signature) =>
        {
            if (signature is null)
                throw new ReleaseRefusedException("The release is unsigned, but a release public key is configured.");
            if (!Verify(publicKey, manifest, Parse(signature)))
                throw new ReleaseRefusedException("The release manifest's signature does not verify against the configured public key.");
        };

    /// <summary>A base64 key of the right length, or <see cref="FormatException"/> (the value itself is never echoed).</summary>
    public static byte[] DecodeKey(string base64, string what)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException)
        {
            throw new FormatException($"The {what} is not base64.");
        }
        return bytes.Length == KeyLength ? bytes : throw new FormatException($"The {what} must be {KeyLength} bytes (it has {bytes.Length}).");
    }

    private static byte[] Key(ReadOnlySpan<byte> key, string what)
        => key.Length == KeyLength ? key.ToArray() : throw new ArgumentException($"An Ed25519 {what} key is {KeyLength} bytes.");
}
