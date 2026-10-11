using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MintPlayer.Spark.WebPush;

/// <summary>
/// The application server's VAPID identity (RFC 8292): a P-256 key pair, base64url, and the contact the push
/// services may use (<c>mailto:</c> or <c>https:</c>). The public key is what the browser's
/// <c>pushManager.subscribe({ applicationServerKey })</c> takes.
/// </summary>
/// <param name="PublicKey">The uncompressed public point (65 bytes, base64url).</param>
/// <param name="PrivateKey">The private scalar <c>d</c> (32 bytes, base64url). A secret: configuration only.</param>
/// <param name="Subject">The JWT <c>sub</c>: <c>mailto:ops@example.com</c> or an https URL.</param>
public sealed record VapidDetails(string PublicKey, string PrivateKey, string Subject)
{
    /// <summary>Makes a new key pair (run once, keep the private key in a secret store).</summary>
    public static VapidDetails Generate(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(true);
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);
        return new VapidDetails(Base64Url.EncodeToString(publicKey), Base64Url.EncodeToString(parameters.D!), subject);
    }

    /// <summary>The checks a push service would otherwise answer with 400/403 on every send.</summary>
    /// <exception cref="ArgumentException">A key is malformed, the keys are not a pair, or the subject is neither mailto: nor https:.</exception>
    public void Validate()
    {
        using var _ = VapidAuthorization.CreateKey(this);
        if (!(Subject.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || Subject.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The VAPID subject must be a mailto: or https: URI (RFC 8292 §2.1).", nameof(Subject));
    }
}

/// <summary>
/// The RFC 8292 <c>Authorization: vapid t=…, k=…</c> header: an ES256 JWT with <c>aud</c> = the push
/// service's origin, <c>exp</c> and <c>sub</c>. One token per origin is reused while it has more than an hour
/// left (Apple: "don't refresh your JWT more frequently than once per hour").
/// </summary>
public sealed class VapidAuthorization : IDisposable
{
    /// <summary>How far ahead a token expires. RFC 8292 allows at most 24 hours.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);

    private static readonly TimeSpan RenewBefore = TimeSpan.FromHours(1);
    private readonly VapidDetails details;
    private readonly ECDsa key;
    private readonly TimeProvider time;
    private readonly ConcurrentDictionary<string, (string Header, DateTimeOffset Expires)> tokens = new(StringComparer.Ordinal);

    public VapidAuthorization(VapidDetails details, TimeProvider? timeProvider = null)
    {
        details.Validate();
        this.details = details;
        key = CreateKey(details);
        time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The header value for a message to <paramref name="endpoint"/>.</summary>
    public string HeaderFor(Uri endpoint)
    {
        var audience = endpoint.GetLeftPart(UriPartial.Authority);
        var now = time.GetUtcNow();
        if (tokens.TryGetValue(audience, out var cached) && cached.Expires - now > RenewBefore)
            return cached.Header;

        var expires = now + TokenLifetime;
        var header = $"vapid t={CreateToken(audience, expires)}, k={details.PublicKey}";
        tokens[audience] = (header, expires);
        return header;
    }

    internal string CreateToken(string audience, DateTimeOffset expires)
    {
        var head = Base64Url.EncodeToString("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"u8);
        var claims = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = audience,
            ["exp"] = expires.ToUnixTimeSeconds(),
            ["sub"] = details.Subject,
        }));
        var signingInput = head + "." + claims;
        // ECDsa.SignData's default format is IEEE P1363 (r ‖ s, 64 bytes): exactly what JWS ES256 wants. DER is the pitfall.
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return signingInput + "." + Base64Url.EncodeToString(signature);
    }

    internal static ECDsa CreateKey(VapidDetails details)
    {
        var publicKey = WebPushEncryption.DecodeKey(details.PublicKey, 65, "VAPID public");
        var privateKey = WebPushEncryption.DecodeKey(details.PrivateKey, 32, "VAPID private");
        if (publicKey[0] != 0x04)
            throw new ArgumentException("The VAPID public key is not an uncompressed P-256 point.");
        ECDsa key;
        try
        {
            key = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
                D = privateKey,
            });
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            throw new ArgumentException("The VAPID keys are not a P-256 key pair.");
        }

        // A private key that does not belong to the public key imports on some platforms; a signature it makes
        // then fails at every push service (403). Refuse it here.
        var probe = "vapid-key-check"u8.ToArray();
        using var verifier = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] } });
        if (!verifier.VerifyData(probe, key.SignData(probe, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256))
        {
            key.Dispose();
            throw new ArgumentException("The VAPID private key does not belong to the public key.");
        }
        return key;
    }

    public void Dispose() => key.Dispose();
}
