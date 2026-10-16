namespace MintPlayer.Spark.Messaging.Models;

public class HandlerExecution
{
    /// <summary>
    /// Assembly-qualified type name of the IRecipient implementation.
    /// </summary>
    public string HandlerType { get; set; } = string.Empty;

    public EHandlerStatus Status { get; set; }

    /// <summary>
    /// Genuine failures: the handler threw (or timed out). Each costs one unit of
    /// <see cref="SparkMessage.MaxAttempts"/>. A host dying mid-handler is counted once on the message,
    /// <see cref="SparkMessage.ReclaimCount"/>, and charged to every unfinished handler by <c>RetryBudget</c>.
    /// A cancellation because the host is shutting down is not a failure and is not counted.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Rate-limit parks (<c>RetryAfterException</c>, R30). Each costs a fraction of an attempt:
    /// the handler is dead-lettered when
    /// <c>AttemptCount + RateLimitParks / RateLimitParksPerAttempt &gt;= MaxAttempts</c> (integer division),
    /// see <c>RetryBudget</c>.
    /// </summary>
    public int RateLimitParks { get; set; }

    /// <summary>
    /// When the handler's current rate-limit park ends; null when it is not parked for a rate limit.
    /// Cleared when the handler completes or fails for another reason.
    /// </summary>
    public DateTime? RateLimitedUntilUtc { get; set; }

    public string? LastError { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Optional checkpoint data for handlers that support partial progress.
    /// Stored as JSON. The handler is responsible for serializing/deserializing.
    /// </summary>
    public string? Checkpoint { get; set; }

    /// <summary>
    /// Whether this handler wrote an <c>IMessageProgress</c> sidecar
    /// (<c>{messageId}/progress/{handlerIndex}</c>), so the message's expiry is copied onto it when
    /// the message becomes terminal.
    /// </summary>
    public bool HasProgress { get; set; }
}
