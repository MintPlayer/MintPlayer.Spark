namespace MintPlayer.Spark.Messaging.Models;

/// <summary>
/// Why a message ended at <see cref="EMessageStatus.DeadLettered"/>. The status stays the single
/// terminal-failure value; this says which road led there.
/// </summary>
public enum EDeadLetterReason
{
    /// <summary>A handler failed <see cref="SparkMessage.MaxAttempts"/> times.</summary>
    MaxAttempts,

    /// <summary>
    /// Nothing a retry could fix: a handler threw <c>NonRetryableException</c>, or the message
    /// itself was unusable (type outside the allow-list, unresolvable, empty payload).
    /// </summary>
    NonRetryable,

    /// <summary>
    /// <see cref="SparkMessage.ExpiresAtUtc"/> passed — or would have passed before the next retry or
    /// throttle slot — so the message was never (fully) handled.
    /// </summary>
    Expired,
}
