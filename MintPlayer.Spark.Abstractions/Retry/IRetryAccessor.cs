namespace MintPlayer.Spark.Abstractions.Retry;

public interface IRetryAccessor
{
    /// <summary>
    /// The result from the user's previous retry response for the current step.
    /// Null on the first invocation, or when a new (not-yet-answered) step is reached.
    /// </summary>
    RetryResult? Result { get; }

    /// <summary>
    /// Interrupts the current action and requests the frontend to display
    /// a confirmation/dialog modal. On the first pass this method does not return
    /// (it throws internally). On replay of an already-answered step it returns
    /// normally and populates <see cref="Result"/>.
    ///
    /// If <paramref name="options"/> does not contain "Cancel", the framework auto-appends it.
    /// The frontend always re-invokes with the result (including cancellation).
    /// </summary>
    void Action(
        string title,
        string[] options,
        string? defaultOption = null,
        PersistentObject? persistentObject = null,
        string? message = null
    );
}
