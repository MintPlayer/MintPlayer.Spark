namespace MintPlayer.Spark.Messaging.Abstractions;

/// <summary>
/// Scoped, optional record of which steps of the current handler have completed, so a retry can
/// skip them. Use it when one message does several externally visible things — one mail per
/// recipient, one API call per item — and a retry must not repeat the ones that already happened.
/// <para>
/// Progress is kept per message <b>and per handler</b> in a sidecar document
/// (<c>{messageId}/progress/{handlerIndex}</c>, collection <c>SparkMessageProgresses</c>), never on
/// the message itself, and it expires together with its message. Steps are appended by patch.
/// </para>
/// <para>
/// Mark a step done <i>after</i> its effect happened. A crash between the effect and the mark repeats
/// that one step on retry: this narrows at-least-once, it does not make it exactly-once.
/// </para>
/// </summary>
public interface IMessageProgress
{
    /// <summary>Whether <paramref name="step"/> was marked done by an earlier attempt (or this one).</summary>
    Task<bool> IsDoneAsync(string step, CancellationToken cancellationToken = default);

    /// <summary>Records <paramref name="step"/> as done and saves immediately.</summary>
    Task MarkDoneAsync(string step, CancellationToken cancellationToken = default);
}
