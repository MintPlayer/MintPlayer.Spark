namespace MintPlayer.Spark.Messaging.Abstractions;

/// <summary>
/// Scoped service available within message handlers for saving progress checkpoints.
/// Each call overwrites the previous checkpoint for the current handler execution.
/// On retry, the checkpoint is passed to <see cref="ICheckpointRecipient{TMessage}.HandleAsync"/>
/// so the handler can resume from where it left off.
/// </summary>
public interface IMessageCheckpoint
{
    /// <summary>
    /// Persists a checkpoint string for the current handler execution.
    /// Can be called multiple times — each call overwrites the previous checkpoint.
    /// </summary>
    Task SaveAsync(string checkpoint, CancellationToken cancellationToken = default);
}
