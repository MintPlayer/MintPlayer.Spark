using System.Security.Cryptography;
using NSec.Cryptography;

namespace MintPlayer.Webhooks;

/// <summary>An ed25519 private key for <c>v2a</c>: the 32-byte seed, written as <c>whsk_</c> + base64.</summary>
public sealed class WebhookSigningKey
{
    public const string Prefix = "whsk_";
    private readonly byte[] seed;

    private WebhookSigningKey(byte[] seed) => this.seed = seed;

    public static WebhookSigningKey Generate() => new(RandomNumberGenerator.GetBytes(32));

    public static WebhookSigningKey Parse(string value) => new(Decode(value, Prefix, "signing key"));

    public WebhookPublicKey PublicKey
    {
        get
        {
            using var key = Import();
            return WebhookPublicKey.FromBytes(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        }
    }

    internal Key Import() => Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey);

    /// <summary>The <c>whsk_</c> form. This is the private key: never log it.</summary>
    public override string ToString() => Prefix + Convert.ToBase64String(seed);

    internal static byte[] Decode(string value, string prefix, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var encoded = value.Trim();
        if (encoded.StartsWith(prefix, StringComparison.Ordinal)) encoded = encoded[prefix.Length..];
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new FormatException($"A webhook {what} is base64 after the {prefix} prefix."); }
        if (bytes.Length != 32) throw new FormatException($"A webhook {what} is 32 bytes; this one is {bytes.Length}.");
        return bytes;
    }
}

/// <summary>An ed25519 public key for <c>v2a</c>, written as <c>whpk_</c> + base64.</summary>
public sealed class WebhookPublicKey
{
    public const string Prefix = "whpk_";
    private readonly byte[] bytes;

    private WebhookPublicKey(byte[] bytes) => this.bytes = bytes;

    public static WebhookPublicKey Parse(string value) => new(WebhookSigningKey.Decode(value, Prefix, "public key"));

    internal static WebhookPublicKey FromBytes(byte[] bytes) => new(bytes);

    internal PublicKey Import() => NSec.Cryptography.PublicKey.Import(SignatureAlgorithm.Ed25519, bytes, KeyBlobFormat.RawPublicKey);

    public override string ToString() => Prefix + Convert.ToBase64String(bytes);
}

/// <summary>Standard Webhooks <c>v2a</c>: ed25519 over SHA-256(<c>endpoint_id.msg_id.timestamp.payload</c>).</summary>
public sealed class Ed25519WebhookSigner : IWebhookSigner
{
    public const string SchemeVersion = "v2a";
    private readonly WebhookSigningKey key;

    public Ed25519WebhookSigner(WebhookSigningKey key) => this.key = key ?? throw new ArgumentNullException(nameof(key));

    public string Version => SchemeVersion;

    public byte[] Sign(WebhookSigningInput input)
    {
        if (string.IsNullOrEmpty(input.EndpointId))
            throw new InvalidOperationException("A v2a signature covers the endpoint id; the signing input has none.");
        using var privateKey = key.Import();
        return SignatureAlgorithm.Ed25519.Sign(privateKey, SHA256.HashData(input.GetContentWithEndpoint(input.EndpointId)));
    }
}

/// <summary>Accepts a <c>v2a</c> signature by <paramref name="publicKey"/> for this receiver's <paramref name="endpointId"/>.</summary>
public sealed class Ed25519SignatureVerifier : IWebhookSignatureVerifier
{
    private readonly PublicKey key;
    private readonly string endpointId;

    public Ed25519SignatureVerifier(WebhookPublicKey publicKey, string endpointId)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        key = publicKey.Import();
        this.endpointId = endpointId;
    }

    public string Version => Ed25519WebhookSigner.SchemeVersion;

    public bool Verify(WebhookSigningInput input, byte[] signature)
        => SignatureAlgorithm.Ed25519.Verify(key, SHA256.HashData(input.GetContentWithEndpoint(endpointId)), signature);
}

public static class Ed25519WebhookVerifierOptionsExtensions
{
    /// <summary>Accept <c>v2a</c> signatures. <paramref name="endpointId"/> is the id the producer shows for this endpoint.</summary>
    public static TOptions AddEd25519PublicKey<TOptions>(this TOptions options, string publicKey, string endpointId)
        where TOptions : WebhookVerifierOptions
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        options.Verifiers.Add(new Ed25519SignatureVerifier(WebhookPublicKey.Parse(publicKey), endpointId));
        return options;
    }
}
