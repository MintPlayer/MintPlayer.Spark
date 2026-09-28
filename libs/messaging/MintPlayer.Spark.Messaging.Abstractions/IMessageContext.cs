namespace MintPlayer.Spark.Messaging.Abstractions;

/// <summary>
/// Scoped service describing the message the current handler is running for. Resolve it inside an
/// <see cref="IRecipient{TMessage}"/>; outside a handler every member throws.
/// <para>
/// The id is stable across retries of the same message, which is what makes it usable as an
/// idempotency key towards the outside world (a mail <c>Message-ID</c>, a VERP token).
/// </para>
/// </summary>
public interface IMessageContext
{
    /// <summary>The <c>SparkMessages/…</c> document id of the message being handled.</summary>
    string MessageId { get; }

    /// <summary>The queue the message was published to.</summary>
    string QueueName { get; }

    /// <summary>How many times the message has been picked up, including this pickup.</summary>
    int AttemptCount { get; }

    /// <summary>The expiry set at publish (<see cref="BroadcastOptions.ExpiresAtUtc"/>), if any.</summary>
    DateTime? ExpiresAtUtc { get; }
}
