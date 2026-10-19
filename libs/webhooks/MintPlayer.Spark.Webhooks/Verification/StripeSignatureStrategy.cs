using System.Globalization;
using System.Text;

namespace MintPlayer.Spark.Webhooks;

/// <summary>
/// Stripe's webhook signature: <c>Stripe-Signature: t={unix seconds},v1={hex},v1={hex}…</c>, where
/// each <c>v1</c> is HMAC-SHA256 over <c>{t}.{exact body}</c> keyed with the endpoint's signing secret
/// (<c>whsec_…</c>, used as UTF-8 text). A delivery is accepted when any <c>v1</c> matches and
/// <c>t</c> is within <see cref="Tolerance"/> of now, in either direction.
/// </summary>
/// <remarks>
/// <para>
/// Stripe sends several <c>v1</c> entries while a signing secret is being rolled; any one may match.
/// Other schemes (<c>v0</c>, test-mode extras) are ignored. Source:
/// <see href="https://docs.stripe.com/webhooks#verify-manually">docs.stripe.com/webhooks</see>.
/// </para>
/// <para>
/// The signature is checked before the timestamp, so a genuine but old delivery is reported as
/// <see cref="WebhookRejectionReason.OutsideTolerance"/> (a replay) rather than as a bad signature.
/// The <c>t</c> value is signed as the text Stripe sent, never re-formatted from the parsed number.
/// </para>
/// </remarks>
public sealed class StripeSignatureStrategy : IWebhookSignatureStrategy
{
    /// <summary>The header Stripe signs into.</summary>
    public const string HeaderName = "Stripe-Signature";

    /// <summary>Stripe's own libraries default to five minutes.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    private readonly string? secret;
    private readonly TimeProvider timeProvider;

    /// <summary>A strategy keyed with <paramref name="secret"/>.</summary>
    /// <param name="secret">The endpoint's signing secret (<c>whsec_…</c>).</param>
    /// <param name="tolerance">How far <c>t</c> may be from now; <see cref="DefaultTolerance"/> when null.</param>
    /// <param name="timeProvider">The clock; <see cref="TimeProvider.System"/> when null.</param>
    public StripeSignatureStrategy(string? secret, TimeSpan? tolerance = null, TimeProvider? timeProvider = null)
    {
        if (tolerance is { } t && t <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "The tolerance must be positive.");
        this.secret = secret;
        Tolerance = tolerance ?? DefaultTolerance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>How far the signed timestamp may be from now.</summary>
    public TimeSpan Tolerance { get; }

    /// <inheritdoc />
    public WebhookVerification Verify(WebhookRequest request)
    {
        if (string.IsNullOrEmpty(secret))
            return WebhookVerification.Rejected(WebhookRejectionReason.NoSecretConfigured);

        var header = request.Header(HeaderName);
        if (string.IsNullOrEmpty(header))
            return WebhookVerification.Rejected(WebhookRejectionReason.MissingSignature);

        string? timestampText = null;
        var signatures = new List<string>();
        foreach (var item in header.Split(','))
        {
            var separator = item.IndexOf('=');
            if (separator <= 0)
                continue;
            var key = item[..separator].Trim();
            var value = item[(separator + 1)..].Trim();
            if (key == "t")
                timestampText = value;
            else if (key == "v1" && value.Length > 0)
                signatures.Add(value);
        }

        if (timestampText is null
            || !long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)
            || signatures.Count == 0)
            return WebhookVerification.Rejected(WebhookRejectionReason.MalformedSignature);

        // {t}.{body}, as bytes: the body is appended untouched.
        var prefix = Encoding.UTF8.GetBytes(timestampText + ".");
        var signedPayload = new byte[prefix.Length + request.Body.Length];
        prefix.CopyTo(signedPayload, 0);
        request.Body.CopyTo(signedPayload, prefix.Length);

        var expected = HmacSignatureStrategy.Encode(
            HmacSignatureStrategy.Hash(WebhookHmacAlgorithm.Sha256, Encoding.UTF8.GetBytes(secret), signedPayload),
            WebhookSignatureEncoding.Hex);

        // Every candidate is compared, so the time taken does not say which one matched.
        var matched = false;
        foreach (var signature in signatures)
            matched |= HmacSignatureStrategy.FixedTimeEquals(signature, expected);
        if (!matched)
            return WebhookVerification.Rejected(WebhookRejectionReason.InvalidSignature);

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (Math.Abs(now - timestamp) > (long)Tolerance.TotalSeconds)
            return WebhookVerification.Rejected(WebhookRejectionReason.OutsideTolerance);

        return WebhookVerification.Signed();
    }

    /// <summary>
    /// The <c>Stripe-Signature</c> value Stripe would send for <paramref name="body"/> at
    /// <paramref name="timestamp"/>. For tests and for an in-process test provider that simulates Stripe.
    /// </summary>
    public static string ComputeHeader(string secret, DateTimeOffset timestamp, ReadOnlySpan<byte> body)
    {
        var t = timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var prefix = Encoding.UTF8.GetBytes(t + ".");
        var signedPayload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signedPayload, 0);
        body.CopyTo(signedPayload.AsSpan(prefix.Length));
        var v1 = HmacSignatureStrategy.Encode(
            HmacSignatureStrategy.Hash(WebhookHmacAlgorithm.Sha256, Encoding.UTF8.GetBytes(secret), signedPayload),
            WebhookSignatureEncoding.Hex);
        return $"t={t},v1={v1}";
    }
}
