namespace MintPlayer.Spark.Messaging.Abstractions;

public interface IRecipient<in TMessage>
{
    Task HandleAsync(TMessage message, CancellationToken cancellationToken = default);
}
