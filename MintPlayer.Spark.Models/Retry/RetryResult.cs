namespace MintPlayer.Spark.Abstractions.Retry;

public sealed class RetryResult
{
    /// <summary>
    /// The label of the button the user clicked.
    /// </summary>
    public required string Option { get; init; }

    /// <summary>
    /// The step index this result corresponds to (0-based).
    /// Managed automatically by the framework.
    /// </summary>
    public int Step { get; init; }

    /// <summary>
    /// The PersistentObject with attribute values as filled in by the user.
    /// Null if no PersistentObject was shown in the modal.
    /// </summary>
    public PersistentObject? PersistentObject { get; init; }
}
