using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MintPlayer.Webhooks;

public class WebhookVerifierOptions
{
    /// <summary><c>whsec_</c> secrets accepted for <c>v1</c>. Keep the old one here while a secret rotates.</summary>
    public IList<string> Secrets { get; } = new List<string>();

    /// <summary>Other schemes, e.g. <c>v2a</c> from MintPlayer.Webhooks.Ed25519 (<c>AddEd25519PublicKey</c>).</summary>
    public IList<IWebhookSignatureVerifier> Verifiers { get; } = new List<IWebhookSignatureVerifier>();

    /// <summary>How far <c>webhook-timestamp</c> may be from now, either way. Default 5 minutes.</summary>
    public TimeSpan Tolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The clock. Tests replace it.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
}

public enum WebhookVerificationFailure
{
    None,
    MissingHeaders,
    InvalidTimestamp,
    TimestampOutOfTolerance,
    InvalidSignature,
}

public sealed class WebhookVerificationResult
{
    private WebhookVerificationResult(WebhookVerificationFailure failure, string? messageId, long timestamp)
    {
        Failure = failure;
        MessageId = messageId;
        Timestamp = timestamp;
    }

    public bool IsValid => Failure == WebhookVerificationFailure.None;
    public WebhookVerificationFailure Failure { get; }
    public string? MessageId { get; }
    public long Timestamp { get; }

    internal static WebhookVerificationResult Valid(string id, long ts) => new(WebhookVerificationFailure.None, id, ts);
    internal static WebhookVerificationResult Invalid(WebhookVerificationFailure failure, string? id = null, long ts = 0) => new(failure, id, ts);
}

/// <summary>Verifies a Standard Webhooks request: headers present, timestamp within tolerance, one signature valid.</summary>
public sealed class WebhookVerifier
{
    private readonly Dictionary<string, List<IWebhookSignatureVerifier>> verifiers;
    private readonly TimeSpan tolerance;
    private readonly Func<DateTimeOffset> clock;

    public WebhookVerifier(WebhookVerifierOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        var all = new List<IWebhookSignatureVerifier>(options.Verifiers);
        if (options.Secrets.Count > 0)
            all.Add(new HmacSignatureVerifier(options.Secrets.Select(WebhookSecret.Parse)));
        // Fail closed: a receiver with nothing to verify against is a configuration error, never "accept all".
        if (all.Count == 0)
            throw new InvalidOperationException("A webhook receiver needs at least one secret or verifier.");

        verifiers = all.GroupBy(v => v.Version, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        tolerance = options.Tolerance;
        clock = options.Clock;
    }

    /// <param name="getHeader">Header lookup; it should be case-insensitive (HTTP header names are).</param>
    /// <param name="body">The raw request body bytes.</param>
    public WebhookVerificationResult Verify(Func<string, string?> getHeader, byte[] body)
    {
        var id = getHeader(WebhookHeaders.Id);
        var timestampText = getHeader(WebhookHeaders.Timestamp);
        var signatureText = getHeader(WebhookHeaders.Signature);
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(timestampText) || string.IsNullOrEmpty(signatureText))
            return WebhookVerificationResult.Invalid(WebhookVerificationFailure.MissingHeaders);

        if (!long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
            return WebhookVerificationResult.Invalid(WebhookVerificationFailure.InvalidTimestamp, id);

        var now = clock().ToUnixTimeSeconds();
        if (Math.Abs(now - timestamp) > (long)tolerance.TotalSeconds)
            return WebhookVerificationResult.Invalid(WebhookVerificationFailure.TimestampOutOfTolerance, id, timestamp);

        var input = new WebhookSigningInput(id!, timestamp, body);
        foreach (var signature in WebhookSignatureHeader.Parse(signatureText))
        {
            if (!verifiers.TryGetValue(signature.Key, out var candidates)) continue;
            foreach (var verifier in candidates)
                if (verifier.Verify(input, signature.Value))
                    return WebhookVerificationResult.Valid(id!, timestamp);
        }
        return WebhookVerificationResult.Invalid(WebhookVerificationFailure.InvalidSignature, id, timestamp);
    }
}
