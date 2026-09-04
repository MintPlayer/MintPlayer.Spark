namespace MintPlayer.Spark.Messaging.Abstractions;

/// <summary>
/// Names the lane being declared. The mode is chosen next, on <see cref="IQueueBuilder"/>.
/// </summary>
public interface ILaneBuilder
{
    /// <summary>
    /// The lane <typeparamref name="TMessage"/> belongs to, read from its <c>[MessageQueue]</c>
    /// attribute (or derived from the type when it has none).
    /// </summary>
    /// <remarks>
    /// Preferred over the string overload: the lane name cannot drift apart from the message that
    /// binds to it, and the type is registered as belonging to the lane, which is what lets startup
    /// check that an ordered lane has a partition selector for it.
    /// </remarks>
    IQueueBuilder Queue<TMessage>();

    /// <summary>A lane named directly — for framework lanes and dynamic names.</summary>
    IQueueBuilder Queue(string laneName);
}
