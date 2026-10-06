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
    /// The <paramref name="options"/> are sent to the frontend exactly as specified, and shown as
    /// their own labels, so pass them translated.
    /// </summary>
    /// <param name="cancellable">
    /// Offers a Cancel as well: the client adds its own button, labelled in the user's language
    /// (<c>common.cancel</c>), and answers it, or the modal being closed, with
    /// <see cref="RetryResult.CancelOption"/>. Without it a closed modal abandons the request and
    /// the action never hears of it.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/> contains <see cref="RetryResult.CancelOption"/>: Cancel is an
    /// answer, not a label, so it is asked for with <paramref name="cancellable"/>.
    /// </exception>
    void Action(
        string title,
        string[] options,
        string? defaultOption = null,
        PersistentObject? persistentObject = null,
        string? message = null,
        bool cancellable = false
    );

    /// <summary>
    /// Asks the browser to run the client method registered under <paramref name="clientMethod"/>
    /// (<c>provideSparkClientMethods</c>) and hand its result back to this action. The same step
    /// pump as <see cref="Action"/>: on an unanswered step this throws and never returns; on the
    /// replay of an answered step it returns and <see cref="Result"/> holds the answer —
    /// <see cref="RetryResult.Value"/> on success, <see cref="RetryResult.Option"/> <c>"Cancel"</c>
    /// when the method was unknown, unsupported, rejected or threw.
    /// </summary>
    /// <param name="clientMethod">The registered name, e.g. <c>"webauthn.create"</c>.</param>
    /// <param name="arguments">
    /// Builds the method's argument. Runs <b>only</b> for an unanswered step.
    /// </param>
    /// <remarks>
    /// ⚠️ <b>The action re-runs from the top on every pass.</b> Arguments are a factory, not a value,
    /// because building them may have side effects — a WebAuthn challenge issued again on the answering
    /// pass would overwrite the state the answer must be checked against. Anything else an action does
    /// before a retry step must be side-effect free or idempotent for the same reason.
    /// </remarks>
    Task Invoke(string clientMethod, Func<Task<object?>> arguments);
}
