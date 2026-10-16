namespace MintPlayer.Spark.Messaging.Abstractions;

/// <summary>
/// Thrown by a recipient that was told to come back later — a 429, a rate-limit 403, a
/// <c>Retry-After</c> header — rather than one that failed. The handler is parked for
/// <c>max(</c>the queue's backoff delay for its current attempt count<c>, </c><see cref="Delay"/><c>)</c>,
/// and the park is counted as a <b>rate-limit park</b>, not as an attempt.
/// <para>
/// Parks are a weighted budget (dependency-updates PRD §13.13.3, R30): a handler is dead-lettered when
/// <c>AttemptCount + RateLimitParks / RateLimitParksPerAttempt &gt;= MaxAttempts</c> (integer division).
/// With the defaults (<c>MaxAttempts</c> 5, <c>RateLimitParksPerAttempt</c> 5) that is 5 genuine errors,
/// 25 parks, or any mix such as 2 errors and 15 parks. An endlessly throttled message therefore still
/// ends, with dead-letter reason <c>RateLimited</c> when the parks made up most of its budget.
/// </para>
/// <para>
/// The message's expiry (<see cref="BroadcastOptions.ExpiresAtUtc"/>) still applies: a park that would
/// end after it dead-letters the message as <c>Expired</c>. Thrown synchronously or from the task, and
/// wrapped or not (as an inner exception), it is recognised the same way as
/// <see cref="NonRetryableException"/>.
/// </para>
/// </summary>
public class RetryAfterException : Exception
{
    /// <param name="delay">The earliest the handler should run again. Negative values count as zero.</param>
    /// <param name="message">Recorded as the handler's <c>LastError</c>; a default naming the delay when null.</param>
    /// <param name="inner">The exception that carried the hint, if any.</param>
    public RetryAfterException(TimeSpan delay, string? message = null, Exception? inner = null)
        : base(message ?? $"Asked to retry after {delay}", inner)
    {
        Delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    /// <summary>The earliest the handler should run again, as the remote side asked.</summary>
    public TimeSpan Delay { get; }
}
