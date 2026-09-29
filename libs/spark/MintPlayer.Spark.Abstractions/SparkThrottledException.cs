namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Refuses a write because the caller has done it too often — answered <b>429</b>, never 404 or 400
/// (#460, §3.1: refusals are distinguishable). Throw it from an interceptor or an Actions class for a
/// business-level quota such as "a new account may post five times a day".
/// </summary>
/// <remarks>
/// Unlike the rate limiter's 429 (which answers before any endpoint runs, with an empty body), this
/// one travels in the response envelope: <c>{ result: { error, retryAfterSeconds } }</c>, plus a
/// <c>Retry-After</c> header when <see cref="RetryAfter"/> is known. It is raised only after the row
/// gate passed, so it discloses nothing about rows the caller cannot see.
/// </remarks>
public sealed class SparkThrottledException : Exception
{
    /// <summary>How long until the quota admits the caller again, when known.</summary>
    public TimeSpan? RetryAfter { get; }

    public SparkThrottledException(string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        RetryAfter = retryAfter;
    }
}
