namespace MintPlayer.Spark.Messaging.Abstractions;

/// <summary>
/// Extended recipient interface for handlers that process collections and want
/// resume-from-failure semantics. When a checkpoint exists from a previous attempt,
/// the framework calls the checkpoint overload so the handler can resume where it left off.
/// </summary>
public interface ICheckpointRecipient<in TMessage> : IRecipient<TMessage>
{
    /// <summary>
    /// Called instead of <see cref="IRecipient{TMessage}.HandleAsync"/> when a checkpoint
    /// exists from a previous attempt. The handler should resume processing from where it left off.
    /// </summary>
    Task HandleAsync(TMessage message, string checkpoint, CancellationToken cancellationToken = default);
}
