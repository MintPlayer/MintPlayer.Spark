namespace MintPlayer.Spark.Webhooks.Receiver;

/// <summary>
/// A verified webhook event, as a durable Spark message. Implement <c>IRecipient&lt;WebhookMessage&lt;ReportCompleted&gt;&gt;</c>
/// to handle one event type. Retries follow the Messaging queue's rules; the producer already has its 2xx.
/// </summary>
public sealed class WebhookMessage<T>
{
    /// <summary>The receiver path it came in on, so one app can tell its producers apart.</summary>
    public string Receiver { get; set; } = "";

    /// <summary><c>webhook-id</c>: the same on every retry and redelivery of this event.</summary>
    public string MessageId { get; set; } = "";

    /// <summary>The event type, e.g. <c>report.completed</c>.</summary>
    public string Type { get; set; } = "";

    /// <summary>When the producer says the event happened.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    public T Data { get; set; } = default!;
}

/// <summary>An event whose type is not in the receiver's <c>EventTypes</c> (a newer catalog), with its raw <c>data</c>.</summary>
public sealed class UnknownWebhookMessage
{
    public string Receiver { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>The <c>data</c> object as JSON text.</summary>
    public string DataJson { get; set; } = "";
}
