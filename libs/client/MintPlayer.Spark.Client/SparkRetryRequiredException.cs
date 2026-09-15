using System.Net;

namespace MintPlayer.Spark.Client;

/// <summary>
/// Thrown when the server asks a question (<c>449</c>) that nothing answered — either no
/// <see cref="SparkRetryHandler"/> was supplied, or the one supplied returned <c>null</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is not a failure of the request. It is the request stopping to ask something, and it carries
/// the question in <see cref="Prompt"/> so a caller can read it, log it, or show it. Answering means
/// calling the same method again with a handler: the server replays the hook from the top and feeds
/// it the answers, so a second call is the protocol rather than a workaround.
/// </para>
/// <para>
/// ⚠️ Before this existed, a <c>449</c> from anything but <c>actions/execute</c> surfaced as a plain
/// <see cref="SparkClientException"/> with a status nobody recognises and an envelope nobody parsed.
/// Nine endpoints can prompt; the client understood one. Measured by the S1 wire-fidelity spike.
/// </para>
/// </remarks>
public sealed class SparkRetryRequiredException : SparkClientException
{
    public SparkRetryRequiredException(RetryActionPayload prompt, string? responseBody, int answered)
        : base((HttpStatusCode)449, responseBody, BuildMessage(prompt, answered))
    {
        Prompt = prompt;
        AnsweredSoFar = answered;
        // Everything except the prompt itself — that is what Prompt is for, and carrying it twice
        // would make "did the hook say anything?" answerable only by filtering.
        Operations = [.. SparkClientOperations.Parse(responseBody).Where(o => o is not SparkRetryOperation)];
    }

    /// <summary>The question the server is asking.</summary>
    public RetryActionPayload Prompt { get; }

    /// <summary>
    /// Client operations that accompanied the question — everything in the envelope except the
    /// prompt itself, in emission order.
    /// </summary>
    /// <remarks>
    /// ⚠️ A prompt nobody answers still carries what the hook said before asking. Dropping these
    /// with the exception would lose the "saved 3 of 4 rows, what about the fourth?" half of a
    /// conversation, which is the half that explains the question.
    /// </remarks>
    public IReadOnlyList<SparkClientOperation> Operations { get; }

    /// <summary>
    /// How many prompts were answered before this one went unanswered. Zero when no handler was
    /// supplied at all; non-zero when a handler answered some and then declined.
    /// </summary>
    public int AnsweredSoFar { get; }

    private static string BuildMessage(RetryActionPayload prompt, int answered)
    {
        var options = prompt.Options.Length == 0 ? "(none offered)" : string.Join(" / ", prompt.Options);
        var preamble = answered == 0
            ? "The server asked a question and no retry handler was supplied"
            : $"The server asked a question and the retry handler declined it (after answering {answered})";
        return $"{preamble}. Step {prompt.Step}: \"{prompt.Title}\" — {options}. "
             + "Pass an onRetry handler (or set SparkClient.RetryHandler) to answer it.";
    }
}
