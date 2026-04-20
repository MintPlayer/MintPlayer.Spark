using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.SubscriptionWorker;

/// <summary>
/// Outcome of a single <see cref="RetryNumerator.TrackRetryAsync"/> call.
/// </summary>
/// <param name="WillRetry">True if the document has budget remaining; false if max attempts were exhausted and the document is parked.</param>
/// <param name="AttemptCount">The attempt number that this failure represents (1-based).</param>
/// <param name="NextAttemptAtUtc">The absolute UTC time after which redelivery should happen. Callers MUST project this onto a subscription-query-visible field (e.g. an entity property) — the <c>@refresh</c> metadata alone doesn't gate change-vector-driven re-delivery.</param>
public readonly record struct RetryOutcome(bool WillRetry, long AttemptCount, DateTime NextAttemptAtUtc);

/// <summary>
/// Tracks per-document retry attempts using RavenDB counters and schedules
/// redelivery via the @refresh metadata mechanism.
/// </summary>
public class RetryNumerator
{
    /// <summary>Maximum retry attempts before flagging as permanently failed. Default: 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>RavenDB counter name used to persist attempt count. Default: "SparkRetryAttempts".</summary>
    public string CounterName { get; set; } = "SparkRetryAttempts";

    /// <summary>Base delay for incremental backoff. Actual delay = BaseDelay * attempt. Default: 30 seconds.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Delay applied when max attempts are exhausted (effectively parked). Default: 1 day.</summary>
    public TimeSpan ExhaustedDelay { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Tracks a failed processing attempt for the given entity.
    /// Increments the retry counter and schedules a @refresh for redelivery.
    /// </summary>
    /// <param name="session">The async document session from the subscription batch.</param>
    /// <param name="entity">The entity that failed processing.</param>
    /// <param name="exception">The exception that caused the failure.</param>
    /// <param name="logger">Optional logger for structured logging.</param>
    /// <returns>A <see cref="RetryOutcome"/> describing whether the retry will happen and when.</returns>
    public async Task<RetryOutcome> TrackRetryAsync(
        IAsyncDocumentSession session,
        object entity,
        Exception exception,
        ILogger? logger = null)
    {
        var counters = session.CountersFor(entity);
        counters.Increment(CounterName, 1);

        var currentCount = await counters.GetAsync(CounterName) ?? 1;
        var metadata = session.Advanced.GetMetadataFor(entity);

        if (currentCount < MaxAttempts)
        {
            var delay = GetDelay((int)currentCount);
            var refreshAt = DateTime.UtcNow + delay;
            metadata["@refresh"] = refreshAt.ToString("o");

            logger?.LogWarning(
                exception,
                "Document {Id} failed (attempt {Attempt}/{MaxAttempts}), scheduling retry at {RefreshAt}",
                session.Advanced.GetDocumentId(entity),
                currentCount,
                MaxAttempts,
                refreshAt);

            return new RetryOutcome(WillRetry: true, AttemptCount: currentCount, NextAttemptAtUtc: refreshAt);
        }
        else
        {
            // Max attempts exhausted — park the document
            counters.Delete(CounterName);
            var parkUntil = DateTime.UtcNow + ExhaustedDelay;
            metadata["@refresh"] = parkUntil.ToString("o");

            logger?.LogError(
                exception,
                "Document {Id} permanently failed after {MaxAttempts} attempts, parked until {ParkUntil}",
                session.Advanced.GetDocumentId(entity),
                MaxAttempts,
                parkUntil);

            return new RetryOutcome(WillRetry: false, AttemptCount: currentCount, NextAttemptAtUtc: parkUntil);
        }
    }

    /// <summary>
    /// Clears the retry counter for a successfully processed entity.
    /// </summary>
    public Task ClearRetryAsync(IAsyncDocumentSession session, object entity)
    {
        session.CountersFor(entity).Delete(CounterName);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Computes the delay for a given attempt number using linear incremental backoff.
    /// </summary>
    public TimeSpan GetDelay(int attempt)
    {
        return BaseDelay * Math.Max(attempt, 1);
    }
}
