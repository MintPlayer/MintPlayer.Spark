using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Client;

/// <summary>
/// Answers one retry prompt. Returning <c>null</c> from a <see cref="SparkRetryHandler"/> means
/// "I am not answering this" — the call then fails with <see cref="SparkRetryRequiredException"/>
/// rather than looping.
/// </summary>
public sealed class RetryAnswer
{
    private RetryAnswer(string option, PersistentObject? persistentObject)
    {
        Option = option;
        PersistentObject = persistentObject;
    }

    /// <summary>The label of the option chosen, exactly as it appeared in the prompt's options.</summary>
    public string Option { get; }

    /// <summary>
    /// The prompt's PersistentObject with the caller's values filled in, when the prompt carried one.
    /// Null otherwise.
    /// </summary>
    public PersistentObject? PersistentObject { get; }

    /// <summary>Chooses an option, optionally handing back the filled-in form the prompt carried.</summary>
    public static RetryAnswer Choose(string option, PersistentObject? persistentObject = null)
        => new(option ?? throw new ArgumentNullException(nameof(option)), persistentObject);

    /// <summary>
    /// Cancels the flow. Spark treats <c>"Cancel"</c> as a distinguished answer: the frontend sends it
    /// when the user closes the modal, and a hook that reads it typically returns without acting. It
    /// is <b>not</b> auto-appended to a prompt's options, so a hook that never offers it will reject
    /// this — see <see cref="SparkClient.MaxRetryDepth"/> for the other way a conversation ends.
    /// </summary>
    public static RetryAnswer Cancel() => new("Cancel", null);
}

/// <summary>
/// Answers retry prompts raised while a request is in flight. Called once per prompt, in the order
/// the server raises them.
/// </summary>
/// <remarks>
/// ⚠️ <b>Deliberately a function, not state on the client.</b> The conversation — which prompts have
/// been answered so far — lives in the request being retried, never on <see cref="SparkClient"/>, so
/// two concurrent conversations through one client cannot interleave their answers. That was the one
/// structural idea worth taking from Vidyano's client, and it is why this is a handler rather than a
/// `client.Answer(...)` call.
/// </remarks>
public delegate Task<RetryAnswer?> SparkRetryHandler(RetryActionPayload prompt, CancellationToken cancellationToken);
