using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Apps.Messaging;

public static class SparkAppsQueues
{
    /// <summary>Raised events, fanned out to their destinations.</summary>
    public const string Events = "spark-apps-events";

    /// <summary>One message per delivery attempt chain (D14).</summary>
    public const string Deliveries = "spark-apps-webhooks";
}

/// <summary>An event a site published, written to the outbox in the publisher's session (atomic with the business write).</summary>
[MessageQueue(SparkAppsQueues.Events)]
public sealed class WebhookEventRaised
{
    /// <summary>The <c>webhook-id</c> every destination receives.</summary>
    public string MessageId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string OwnerKind { get; set; } = string.Empty;
    public string OwnerKey { get; set; } = string.Empty;

    /// <summary>The envelope JSON as it will be signed and sent.</summary>
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}

/// <summary>Send one <c>WebhookDelivery</c>. Retried by Messaging with the queue's backoff.</summary>
[MessageQueue(SparkAppsQueues.Deliveries)]
public sealed class WebhookDeliveryMessage
{
    public string DeliveryId { get; set; } = string.Empty;
}
