namespace MintPlayer.Spark.Abstractions.Retry;

public interface IRetryAccessor
{
    /// <summary>
    /// The result from the user's previous retry response for the current step.
    /// Null on the first invocation. Set by each <see cref="Action"/> call
    /// when replaying an already-answered step.
    /// </summary>
    RetryResult? Result { get; }

    /// <summary>
    /// Requests the frontend to display a confirmation/dialog modal.
    /// On the first pass (unanswered step) this method throws internally and never returns.
    /// On replay of an already-answered step it returns normally and populates <see cref="Result"/>.
    ///
    /// The <paramref name="options"/> are sent to the frontend exactly as specified.
    /// "Cancel" is NOT auto-appended. However, when the user closes the modal
    /// (e.g. via the X button), the frontend sends "Cancel" as the chosen option.
    /// </summary>
    void Action(
        string title,
        string[] options,
        string? defaultOption = null,
        PersistentObject? persistentObject = null,
        string? message = null
    );
}
