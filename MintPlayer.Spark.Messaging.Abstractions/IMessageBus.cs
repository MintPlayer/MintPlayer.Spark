namespace MintPlayer.Spark.Messaging.Abstractions;

public interface IMessageBus
{
    Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default);
    Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default);
}
