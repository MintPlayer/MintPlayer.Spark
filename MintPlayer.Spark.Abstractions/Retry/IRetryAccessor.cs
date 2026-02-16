namespace MintPlayer.Spark.Abstractions.Retry;

public interface IRetryAccessor
{
    /// <summary>
    /// The result from the user's previous retry response.
    /// Null on the first invocation. Pre-populated on re-invocation with the
    /// latest answered step's result, so developers can use a guard pattern:
    /// <code>
    /// if (manager.Retry.Result == null)
    ///     manager.Retry.Action(...);
    /// </code>
    /// Also set by each <see cref="Action"/> call for the matching answered step.
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
