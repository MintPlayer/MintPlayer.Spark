namespace MintPlayer.Spark.Messaging.Models;

public class SparkMessage
{
    public string? Id { get; set; }
    public string QueueName { get; set; } = string.Empty;
    public string MessageType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }

    /// <summary>
    /// Number of times this message has been picked up for processing (informational).
    /// Per-handler attempt counts are tracked in <see cref="Handlers"/>.
    /// </summary>
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; }
    public EMessageStatus Status { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Per-handler execution state. Populated when the message is first picked up for processing.
    /// </summary>
    public List<HandlerExecution> Handlers { get; set; } = new();

    /// <summary>
    /// Set by <c>MessageRetrySweeper</c> when <see cref="NextAttemptAtUtc"/> has passed;
    /// cleared by the worker on pickup and when parking the message for another attempt.
    /// This is the subscription-visible redelivery gate: subscription where-clauses cannot
    /// evaluate time (<c>now()</c> silently never matches), so "the backoff has elapsed"
    /// must be materialized as plain field state by a component that CAN evaluate time.
    /// </summary>
    public bool WakeUp { get; set; }

    /// <summary>
    /// When the fallback sweeper last touched this message to trigger subscription
    /// re-evaluation (see <c>MessageRetrySweeper</c>). Informational.
    /// </summary>
    public DateTime? LastWakeUpUtc { get; set; }
}
