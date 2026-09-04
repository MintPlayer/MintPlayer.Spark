namespace MintPlayer.Spark.Messaging.Abstractions;

/// <summary>
/// How a lane delivers its messages. Chosen through <see cref="IQueueBuilder"/> rather than set as a
/// property, so that combinations which contradict each other cannot be expressed.
/// </summary>
public enum QueueDelivery
{
    /// <summary>
    /// Messages sharing a partition key run one at a time, oldest first, and a failing head blocks
    /// <b>only its own partition</b>. Different partitions run concurrently, up to
    /// <c>MaxPartitionsInFlight</c>.
    /// </summary>
    Ordered,

    /// <summary>
    /// No ordering. Up to <c>MaxConcurrency</c> messages run at once and nothing waits behind a
    /// parked message.
    /// </summary>
    Concurrent,
}
