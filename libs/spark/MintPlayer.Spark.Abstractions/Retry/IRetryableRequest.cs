namespace MintPlayer.Spark.Abstractions.Retry;

/// <summary>
/// A request body that can carry the answers to retry prompts raised on an earlier attempt.
/// </summary>
/// <remarks>
/// <para>
/// A retry has two halves, and an endpoint needs <b>both</b> to work:
/// </para>
/// <list type="number">
/// <item><b>accept</b> — the request type implements this interface, and the endpoint feeds the
/// answers to the retry accessor before running any hook.</item>
/// <item><b>emit</b> — the endpoint catches <c>SparkRetryActionException</c> and answers
/// <c>449</c> with a retry operation, rather than letting it escape.</item>
/// </list>
/// <para>
/// ⚠️ Implementing this interface gives an endpoint the <i>accept</i> half only. Both halves were
/// once copy-pasted per endpoint with nothing connecting them, and the result was exactly what you
/// would expect: <c>refresh</c> shipped with accept and no emit, and <c>new</c> / <c>delete-row</c>
/// shipped with neither — so a hook calling <c>Retry.Action(...)</c> on those paths threw straight
/// out of the request pipeline. The matrix in
/// <c>tests/MintPlayer.Spark.Tests/Endpoints/PersistentObject/RetryFromEveryHookTests.cs</c> is what
/// now holds both halves together; keep any new retry-capable endpoint in it.
/// </para>
/// </remarks>
public interface IRetryableRequest
{
    /// <summary>
    /// Answers to prompts raised on previous attempts, keyed by <see cref="RetryResult.Step"/>.
    /// Null or empty on the first attempt.
    /// </summary>
    RetryResult[]? RetryResults { get; }
}
