namespace MintPlayer.Webhooks;

/// <summary>The Standard Webhooks header names (https://www.standardwebhooks.com).</summary>
public static class WebhookHeaders
{
    /// <summary>The message id. Unchanged across retries and redeliveries, so it is the receiver's idempotency key.</summary>
    public const string Id = "webhook-id";

    /// <summary>Unix seconds of this attempt. Signed, so a captured request cannot be replayed outside the tolerance.</summary>
    public const string Timestamp = "webhook-timestamp";

    /// <summary>A space-separated list of <c>version,base64</c> signatures (more than one while a secret rotates).</summary>
    public const string Signature = "webhook-signature";
}
