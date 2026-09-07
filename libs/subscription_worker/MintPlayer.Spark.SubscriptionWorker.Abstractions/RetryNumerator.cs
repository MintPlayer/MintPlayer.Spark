using Microsoft.Extensions.Logging;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.SubscriptionWorker;

/// <summary>
/// Outcome of a single <see cref="RetryNumerator.TrackRetryAsync"/> call.
/// </summary>
/// <param name="WillRetry">True if the document has budget remaining; false if max attempts were exhausted and the document is parked.</param>
/// <param name="AttemptCount">The attempt number that this failure represents (1-based).</param>
/// <param name="NextAttemptAtUtc">The absolute UTC time after which redelivery should happen. Callers MUST project this onto a subscription-query-visible field (e.g. an entity property) and pair it with a sweeper that flips a boolean gate when the time passes — a subscription re-evaluates a document only when the document is written, and a where-clause cannot evaluate time.</param>
public readonly record struct RetryOutcome(bool WillRetry, long AttemptCount, DateTime NextAttemptAtUtc);

/// <summary>
/// Tracks per-document retry attempts using RavenDB counters and computes the backoff
/// schedule. It records <em>when</em> redelivery should happen; it does not cause it.
/// <para>
/// This type used to also write <c>@metadata.@refresh</c>, which was inert: nothing in this
/// repository ever sent <c>ConfigureRefreshOperation</c>, so the server never swept those
/// documents and the write neither caused redelivery nor did any harm. Worse, its own
/// documentation asserted that <c>@refresh</c> "doesn't gate change-vector-driven
/// re-delivery", so the code read as a working mechanism while the comment next to it said
/// it could not work. The writes are gone; the caller-visible contract is
/// <see cref="RetryOutcome.NextAttemptAtUtc"/> plus a sweeper.
/// </para>
/// <para>
/// For the record, that disclaimer was <b>false</b> and the mechanism is available if anyone
/// wants it. Measured 2026-09-07 on RavenDB 7.2: with refresh enabled at 5 s, a document
/// carrying <c>@refresh</c> was correctly withheld from a subscription whose query required
/// <c>not exists(@metadata.@refresh)</c>, then delivered 4.65 s later once the server's sweep
/// removed the metadata. Adopting it would mean sending <c>ConfigureRefreshOperation</c> and
/// asserting that configuration at startup — deliberately not done here, because the
/// sweeper-plus-boolean-gate path already works and is what both messaging and replication
/// use. See <c>docs/messaging_single_subscription_plan.md</c>, spike S4.
/// </para>
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
    /// Tracks a failed processing attempt for the given entity: increments the retry counter
    /// and computes when the next attempt is due. Causing that attempt is the caller's job —
    /// project <see cref="RetryOutcome.NextAttemptAtUtc"/> onto a queryable field and let a
    /// sweeper flip the subscription-visible gate when it passes.
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

        if (currentCount < MaxAttempts)
        {
            var delay = GetDelay((int)currentCount);
            var refreshAt = DateTime.UtcNow + delay;

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
