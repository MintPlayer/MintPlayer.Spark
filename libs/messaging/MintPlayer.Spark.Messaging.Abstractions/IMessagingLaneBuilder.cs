namespace MintPlayer.Spark.Messaging.Abstractions;

/// <summary>
/// Declares the lanes an application uses.
/// </summary>
public interface IMessagingLaneBuilder
{
    /// <summary>
    /// The lane <typeparamref name="TMessage"/> belongs to, read from its <c>[MessageQueue]</c>
    /// attribute (or derived from the type when it has none).
    /// </summary>
    /// <remarks>
    /// Preferred over the string overload: the lane name cannot drift apart from the message that
    /// binds to it.
    /// </remarks>
    IQueueBuilder Queue<TMessage>();

    /// <summary>A lane named directly — for framework lanes and for dynamic names.</summary>
    IQueueBuilder Queue(string laneName);

    /// <summary>
    /// The longest any one partition may be blocked by its own retry schedule before startup
    /// refuses the configuration. Defaults to fifteen minutes.
    /// </summary>
    /// <remarks>
    /// Under <c>Ordered</c> a failing head blocks its partition until it succeeds or dead-letters, so
    /// a schedule's total <i>is</i> that partition's worst-case downtime. A lane that genuinely wants
    /// a longer one says so with <c>AcceptPartitionBlock</c>.
    /// </remarks>
    IMessagingLaneBuilder MaxPartitionBlock(TimeSpan budget);
}
