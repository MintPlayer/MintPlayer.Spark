namespace MintPlayer.Spark.Webhooks;

/// <summary>Why a delivery was refused. Logged, never sent back to the caller (the response is a bare 401).</summary>
public enum WebhookRejectionReason
{
    /// <summary>The source has no secret configured. Fail closed: an empty secret proves nothing about origin.</summary>
    NoSecretConfigured,

    /// <summary>The signature header is absent or empty.</summary>
    MissingSignature,

    /// <summary>The signature header is present but not in the scheme's shape (for Stripe: no <c>t=</c> or no <c>v1=</c>).</summary>
    MalformedSignature,

    /// <summary>No signature in the header matches the body.</summary>
    InvalidSignature,

    /// <summary>The signature matches, but its timestamp is outside the tolerance: a replay, or a badly skewed clock.</summary>
    OutsideTolerance,

    /// <summary>A fetch-by-id delivery carried no resource id, or one that does not match the expected pattern.</summary>
    MissingResourceId,
}

/// <summary>The outcome of <see cref="IWebhookSignatureStrategy.Verify"/>.</summary>
public sealed class WebhookVerification
{
    private WebhookVerification(bool isAccepted, bool payloadVerified, string? resourceId, WebhookRejectionReason? rejection)
    {
        IsAccepted = isAccepted;
        PayloadVerified = payloadVerified;
        ResourceId = resourceId;
        Rejection = rejection;
    }

    /// <summary>Whether the delivery may be handled.</summary>
    public bool IsAccepted { get; }

    /// <summary>
    /// Whether the body itself is authentic. <see langword="true"/> for a signed delivery;
    /// <see langword="false"/> for fetch-by-id, where the body only names a resource and the handler
    /// must load that resource from the provider before believing anything about it.
    /// </summary>
    public bool PayloadVerified { get; }

    /// <summary>The resource id a fetch-by-id delivery named; <see langword="null"/> for a signed delivery.</summary>
    public string? ResourceId { get; }

    /// <summary>Why the delivery was refused; <see langword="null"/> when it was accepted.</summary>
    public WebhookRejectionReason? Rejection { get; }

    /// <summary>The body is signed and the signature matches.</summary>
    public static WebhookVerification Signed() => new(true, true, null, null);

    /// <summary>The body is not signed; it names <paramref name="resourceId"/>, which the handler fetches from the provider.</summary>
    public static WebhookVerification FetchRequired(string resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        return new(true, false, resourceId, null);
    }

    /// <summary>The delivery is refused.</summary>
    public static WebhookVerification Rejected(WebhookRejectionReason reason) => new(false, false, null, reason);
}

/// <summary>
/// How a webhook source proves a delivery came from its provider. One instance per delivery; a
/// strategy gets the request's exact bytes (<see cref="WebhookRequest.Body"/>) and must verify those,
/// never a re-serialized copy.
/// </summary>
/// <remarks>
/// Built-in strategies: <see cref="HmacSignatureStrategy"/> (an HMAC of the body in one header —
/// GitHub, Shopify, most carriers), <see cref="StripeSignatureStrategy"/> (Stripe's timestamped
/// <c>t=…,v1=…</c> scheme with a replay tolerance) and <see cref="FetchByIdStrategy"/> (Mollie: the
/// body carries only an id, nothing is signed, and the handler re-fetches the resource).
/// </remarks>
public interface IWebhookSignatureStrategy
{
    /// <summary>Verifies <paramref name="request"/>. Never throws for a bad delivery: it returns <see cref="WebhookVerification.Rejected"/>.</summary>
    WebhookVerification Verify(WebhookRequest request);
}
