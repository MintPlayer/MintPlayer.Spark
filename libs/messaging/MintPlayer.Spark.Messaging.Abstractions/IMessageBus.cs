namespace MintPlayer.Spark.Messaging.Abstractions;

public interface IMessageBus
{
    Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default);
    Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcasts a message with an explicit queue name override, ignoring the [MessageQueue] attribute.
    /// Used for per-collection queue isolation (e.g., "spark-sync-Cars", "spark-sync-People").
    /// </summary>
    Task BroadcastAsync<TMessage>(TMessage message, string queueName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcasts to an explicit lane after a delay.
    /// </summary>
    /// <remarks>
    /// The delay is a scheduling instruction, not a dependency: a delayed message does not hold its
    /// partition, so messages broadcast during its delay window may run before it.
    /// </remarks>
    Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, string queueName, CancellationToken cancellationToken = default);
}
