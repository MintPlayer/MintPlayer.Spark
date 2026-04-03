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
}
