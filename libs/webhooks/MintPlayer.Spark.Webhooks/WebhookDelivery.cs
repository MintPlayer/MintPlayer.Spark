using System.Text;
using System.Text.Json;
using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Webhooks;

/// <summary>
/// A verified delivery, as a source's <see cref="IWebhookHandler"/> receives it. For a queued source
/// this is also the message on the <c>spark-webhooks</c> queue.
/// </summary>
/// <remarks>
/// <see cref="Body"/> holds the exact bytes the provider sent. In the queue it is stored as base64,
/// which round-trips every byte, so the handler sees what the signature was checked against. Use
/// <see cref="ReadBodyText"/> or <see cref="ReadJson"/> to read it; never round-trip it through an
/// object model and verify again.
/// </remarks>
[MessageQueue(QueueName)]
public sealed class WebhookDelivery
{
    /// <summary>The queue that carries deliveries of queued sources.</summary>
    public const string QueueName = "spark-webhooks";

    /// <summary>The source's registered name (lower case).</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The provider's delivery or event id, when the source names one (<see cref="WebhookSourceBuilder.DeliveryId"/>).</summary>
    public string? DeliveryId { get; init; }

    /// <summary>
    /// The request headers, minus <c>Cookie</c> and <c>Authorization</c>, which are never stored.
    /// Look a header up with <see cref="Header"/>: the dictionary's own comparer does not survive the queue.
    /// </summary>
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The body exactly as it was received.</summary>
    public byte[] Body { get; init; } = [];

    /// <summary>
    /// <see langword="true"/> when the body is signed and verified. <see langword="false"/> for a
    /// fetch-by-id source: then only <see cref="ResourceId"/> may be used, to fetch the resource.
    /// </summary>
    public bool PayloadVerified { get; init; }

    /// <summary>The resource id a fetch-by-id delivery named.</summary>
    public string? ResourceId { get; init; }

    /// <summary>When the request was received.</summary>
    public DateTimeOffset ReceivedAtUtc { get; init; }

    /// <summary>The value of the header <paramref name="name"/> (case-insensitive), or <see langword="null"/>.</summary>
    public string? Header(string name)
    {
        foreach (var (key, value) in Headers)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return value;
        return null;
    }

    /// <summary>The body decoded as UTF-8.</summary>
    public string ReadBodyText() => Encoding.UTF8.GetString(Body);

    /// <summary>The body parsed as JSON. Dispose the document.</summary>
    public JsonDocument ReadJson() => JsonDocument.Parse(Body);
}

/// <summary>
/// Handles the deliveries of one webhook source. Registered with
/// <see cref="WebhookSourceBuilder.UseHandler{THandler}"/>; one handler per source.
/// </summary>
/// <remarks>
/// A queued handler runs from Spark Messaging: it is retried with backoff when it throws, so it must
/// be idempotent (a provider may also send the same event twice under different delivery ids).
/// Throw <see cref="NonRetryableException"/> for a delivery that can never succeed.
/// </remarks>
public interface IWebhookHandler
{
    /// <summary>Handles one verified delivery.</summary>
    Task HandleAsync(WebhookDelivery delivery, CancellationToken cancellationToken);
}

/// <summary>Where a source's delivery id comes from, for <see cref="WebhookSourceBuilder.DeliveryId"/>.</summary>
public static class WebhookDeliveryIds
{
    /// <summary>The value of a header, e.g. <c>X-GitHub-Delivery</c>.</summary>
    public static Func<WebhookRequest, string?> FromHeader(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return request => request.Header(name) is { Length: > 0 } value ? value : null;
    }

    /// <summary>A top-level string property of a JSON body, e.g. Stripe's event <c>id</c> (<c>evt_…</c>).</summary>
    public static Func<WebhookRequest, string?> FromJsonProperty(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return request =>
        {
            try
            {
                using var document = JsonDocument.Parse(request.Body);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } id
                    ? id
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        };
    }
}
