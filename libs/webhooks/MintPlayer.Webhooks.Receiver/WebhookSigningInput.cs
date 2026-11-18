using System;
using System.Globalization;
using System.Text;

namespace MintPlayer.Webhooks;

/// <summary>
/// What a signature covers: the message id, the attempt's timestamp and the exact body bytes, plus the endpoint id
/// for the asymmetric <c>v2a</c> scheme.
/// </summary>
public sealed class WebhookSigningInput
{
    public WebhookSigningInput(string messageId, long timestamp, byte[] payload, string? endpointId = null)
    {
        if (string.IsNullOrEmpty(messageId)) throw new ArgumentException("A message id is required.", nameof(messageId));
        MessageId = messageId;
        Timestamp = timestamp;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        EndpointId = endpointId;
    }

    public string MessageId { get; }

    /// <summary>Unix seconds.</summary>
    public long Timestamp { get; }

    /// <summary>The raw body bytes, exactly as sent. Signing re-serialised JSON breaks the signature.</summary>
    public byte[] Payload { get; }

    /// <summary>The producer's id for the receiving endpoint. Part of the <c>v2a</c> content only.</summary>
    public string? EndpointId { get; }

    /// <summary><c>msg_id.timestamp.payload</c> (v1).</summary>
    public byte[] GetContent() => Concat(MessageId + "." + Timestamp.ToString(CultureInfo.InvariantCulture) + ".", Payload);

    /// <summary><c>endpoint_id.msg_id.timestamp.payload</c> (v2a).</summary>
    public byte[] GetContentWithEndpoint(string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId)) throw new ArgumentException("An endpoint id is required.", nameof(endpointId));
        return Concat(endpointId + "." + MessageId + "." + Timestamp.ToString(CultureInfo.InvariantCulture) + ".", Payload);
    }

    private static byte[] Concat(string prefix, byte[] payload)
    {
        var head = Encoding.UTF8.GetBytes(prefix);
        var content = new byte[head.Length + payload.Length];
        Buffer.BlockCopy(head, 0, content, 0, head.Length);
        Buffer.BlockCopy(payload, 0, content, head.Length, payload.Length);
        return content;
    }
}
