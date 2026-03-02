using Microsoft.Extensions.Logging;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.SubscriptionWorker;

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
    /// <returns>True if the document will be retried; false if max attempts are exhausted.</returns>
    public async Task<bool> TrackRetryAsync(
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

            return true;
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

            return false;
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
