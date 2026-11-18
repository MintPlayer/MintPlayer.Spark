using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace MintPlayer.Webhooks;

/// <summary>Produces one signature scheme's signature. The producer side.</summary>
public interface IWebhookSigner
{
    /// <summary>The scheme tag in the header: <c>v1</c> (HMAC) or <c>v2a</c> (ed25519).</summary>
    string Version { get; }

    /// <summary>The raw signature bytes over <paramref name="input"/>.</summary>
    byte[] Sign(WebhookSigningInput input);
}

/// <summary>Checks one signature scheme. The receiver side.</summary>
public interface IWebhookSignatureVerifier
{
    string Version { get; }

    bool Verify(WebhookSigningInput input, byte[] signature);
}

/// <summary>Standard Webhooks <c>v1</c>: base64(HMAC-SHA256(secret, <c>msg_id.timestamp.payload</c>)).</summary>
public sealed class HmacWebhookSigner : IWebhookSigner
{
    public const string SchemeVersion = "v1";
    private readonly WebhookSecret secret;

    public HmacWebhookSigner(WebhookSecret secret) => this.secret = secret ?? throw new ArgumentNullException(nameof(secret));

    public string Version => SchemeVersion;

    public byte[] Sign(WebhookSigningInput input)
    {
        using var hmac = new HMACSHA256(secret.Key);
        return hmac.ComputeHash(input.GetContent());
    }
}

/// <summary>Accepts a <c>v1</c> signature made with any of the given secrets (more than one while a secret rotates).</summary>
public sealed class HmacSignatureVerifier : IWebhookSignatureVerifier
{
    private readonly IReadOnlyList<WebhookSecret> secrets;

    public HmacSignatureVerifier(IEnumerable<WebhookSecret> secrets)
    {
        this.secrets = (secrets ?? throw new ArgumentNullException(nameof(secrets))).ToList();
        // Fail closed: a verifier without a secret would accept nothing, and a misconfigured receiver must fail loudly.
        if (this.secrets.Count == 0) throw new ArgumentException("At least one webhook secret is required.", nameof(secrets));
    }

    public string Version => HmacWebhookSigner.SchemeVersion;

    public bool Verify(WebhookSigningInput input, byte[] signature)
    {
        var content = input.GetContent();
        var match = false;
        foreach (var secret in secrets)
        {
            using var hmac = new HMACSHA256(secret.Key);
            // No early exit: every secret is tried, so timing does not reveal which one matched.
            match |= FixedTime.Equals(hmac.ComputeHash(content), signature);
        }
        return match;
    }
}

/// <summary>Builds and parses the <c>webhook-signature</c> header.</summary>
public static class WebhookSignatureHeader
{
    /// <summary>Every signer's <c>version,base64</c>, space-separated.</summary>
    public static string Create(IEnumerable<IWebhookSigner> signers, WebhookSigningInput input)
    {
        var parts = signers.Select(s => s.Version + "," + Convert.ToBase64String(s.Sign(input))).ToList();
        if (parts.Count == 0) throw new ArgumentException("At least one signer is required.", nameof(signers));
        return string.Join(" ", parts);
    }

    /// <summary>The (version, signature) pairs. Malformed entries are skipped, not fatal: another entry may still verify.</summary>
    public static IReadOnlyList<KeyValuePair<string, byte[]>> Parse(string? header)
    {
        var result = new List<KeyValuePair<string, byte[]>>();
        if (string.IsNullOrWhiteSpace(header)) return result;

        foreach (var token in header!.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var comma = token.IndexOf(',');
            if (comma <= 0 || comma == token.Length - 1) continue;
            try
            {
                result.Add(new KeyValuePair<string, byte[]>(token.Substring(0, comma), Convert.FromBase64String(token.Substring(comma + 1))));
            }
            catch (FormatException)
            {
                // not base64: ignore this entry
            }
        }
        return result;
    }
}

internal static class FixedTime
{
    /// <summary><c>CryptographicOperations.FixedTimeEquals</c> is not in netstandard2.0.</summary>
    public static bool Equals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
