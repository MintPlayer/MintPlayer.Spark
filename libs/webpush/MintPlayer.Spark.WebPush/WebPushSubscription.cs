namespace MintPlayer.Spark.WebPush;

/// <summary>
/// A browser's push subscription, exactly as <c>PushSubscription.toJSON()</c> gives it: the push service
/// endpoint and the two keys of <c>keys</c>, base64url.
/// </summary>
/// <param name="Endpoint">The push service URL the message is POSTed to (<c>https://fcm.googleapis.com/…</c>, <c>https://updates.push.services.mozilla.com/…</c>, <c>https://web.push.apple.com/…</c>).</param>
/// <param name="P256dh">The user agent's P-256 public key, uncompressed (65 bytes, base64url).</param>
/// <param name="Auth">The user agent's authentication secret (16 bytes, base64url).</param>
public sealed record WebPushSubscription(string Endpoint, string P256dh, string Auth);

/// <summary>The <c>Urgency</c> header of RFC 8030 §5.3.</summary>
public enum WebPushUrgency
{
    VeryLow,
    Low,
    Normal,
    High,
}

/// <summary>One message to one subscription.</summary>
public sealed class WebPushMessage
{
    /// <summary>
    /// The largest plaintext that fits the 4 096-byte encrypted body every push service accepts
    /// (RFC 8291 §4 header 86 bytes, the delimiter 1, the AES-GCM tag 16).
    /// </summary>
    public const int MaxPayloadBytes = 4096 - WebPushEncryption.HeaderLength - 1 - 16;

    /// <summary>The plaintext the service worker's <c>push</c> event reads (<c>event.data</c>). Empty for a tickle.</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>How long the push service keeps an undelivered message (RFC 8030 <c>TTL</c>). Apple requires one; at most 30 days.</summary>
    public TimeSpan TimeToLive { get; init; } = TimeSpan.FromDays(1);

    /// <summary>RFC 8030 <c>Urgency</c>; omitted when null (the service treats that as <c>normal</c>).</summary>
    public WebPushUrgency? Urgency { get; init; }

    /// <summary>RFC 8030 <c>Topic</c>: a newer message with the same topic replaces an undelivered one. At most 32 base64url characters.</summary>
    public string? Topic { get; init; }

    /// <summary>A UTF-8 text payload, typically JSON.</summary>
    public static WebPushMessage FromText(string text) => new() { Payload = System.Text.Encoding.UTF8.GetBytes(text) };
}
