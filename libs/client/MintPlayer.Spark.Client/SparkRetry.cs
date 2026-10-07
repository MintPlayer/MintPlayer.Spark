using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Client;

/// <summary>
/// Answers one retry prompt. Returning <c>null</c> from a <see cref="SparkRetryHandler"/> means
/// "I am not answering this" — the call then fails with <see cref="SparkRetryRequiredException"/>
/// rather than looping.
/// </summary>
public sealed class RetryAnswer
{
    private RetryAnswer(string option, PersistentObject? persistentObject, object? value = null)
    {
        Option = option;
        PersistentObject = persistentObject;
        Value = value;
    }

    /// <summary>What a client-method step resolved with (<see cref="Return"/>); null otherwise.</summary>
    public object? Value { get; }

    /// <summary>
    /// Answers a client-method step (<see cref="RetryActionPayload.ClientMethod"/>) with what the method
    /// resolved to, as the browser would. The option is <c>"OK"</c>.
    /// </summary>
    public static RetryAnswer Return(object? value) => new("OK", null, value);

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
    /// Cancels the flow. Spark treats <c>"Cancel"</c> as a distinguished answer, never a label: the
    /// browser sends it for its own translated Cancel button and for a closed modal, and a hook that
    /// reads it typically returns without acting. A prompt takes it only when it is
    /// <see cref="RetryActionPayload.Cancellable"/> (or is a client-method step), so a hook that never
    /// asked for it will reject this — see <see cref="SparkClient.MaxRetryDepth"/> for the other way a
    /// conversation ends.
    /// </summary>
    public static RetryAnswer Cancel() => new(Abstractions.Retry.RetryResult.CancelOption, null);
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
