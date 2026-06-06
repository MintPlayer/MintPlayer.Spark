namespace MintPlayer.Spark.Messaging.Models;

public class HandlerExecution
{
    /// <summary>
    /// Assembly-qualified type name of the IRecipient implementation.
    /// </summary>
    public string HandlerType { get; set; } = string.Empty;

    public EHandlerStatus Status { get; set; }

    public int AttemptCount { get; set; }

    public string? LastError { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Optional checkpoint data for handlers that support partial progress.
    /// Stored as JSON. The handler is responsible for serializing/deserializing.
    /// </summary>
    public string? Checkpoint { get; set; }
}
