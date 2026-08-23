using System.Diagnostics;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// Bounded polling for asynchronous work that exposes no completion signal — a subscription worker
/// picking up a document, a file-watcher invalidating a cache, a cron job firing.
/// <para>
/// The point is uniform <b>failure</b> behaviour, not deduplication. The hand-rolled loops this
/// replaces each timed out differently: two threw with good diagnostics, one returned <c>bool</c>
/// so a timeout surfaced as "expected True to be false" with no timing context, and one returned
/// <c>void</c> — swallowing the timeout entirely, so a hung watcher showed up as a confusing
/// assertion three lines later. Everything here throws, and says what it was waiting for and for
/// how long.
/// </para>
/// <para>
/// This is the fallback for work with no observable signal. Prefer a real one where it exists:
/// <see cref="SparkTestDriver.SeedAsync"/> for writes that must be queryable, and
/// <see cref="RavenIndexingExtensions.WaitForIndexingAsync"/> for indexing.
/// </para>
/// </summary>
public static class AsyncWait
{
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(20);

    public static TimeSpan DefaultInterval { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds.
    /// </summary>
    /// <param name="description">
    /// What is being waited for, phrased to complete "timed out waiting for …" — e.g.
    /// <c>"the worker to attach"</c>. This is the whole diagnostic when it fails, so make it
    /// specific.
    /// </param>
    /// <param name="timeout">
    /// A <b>failure</b> bound, not a success bound. This method returns the instant the condition
    /// holds, so a generous timeout costs a passing run nothing — it only changes how long a
    /// genuinely broken case takes to report.
    /// <para>
    /// <b>Set it to many times the expected duration.</b> A tight timeout buys nothing and is how
    /// a suite acquires flaky tests: these run against one RavenDB shared by hundreds of per-test
    /// databases, so any single operation can be far slower than it is in isolation. A cron test
    /// waiting for ten once-per-second occurrences had 30 seconds — a 3x margin — and failed under
    /// full-suite load while passing alone.
    /// </para>
    /// <para>
    /// Never assert on elapsed time to prove something was fast. Under contention that measures
    /// the machine, not the behaviour; assert the property instead.
    /// </para>
    /// </param>
    /// <exception cref="TimeoutException">The condition did not hold within the timeout.</exception>
    public static async Task UntilAsync(
        Func<bool> condition,
        string description,
        TimeSpan? timeout = null,
        TimeSpan? interval = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var effectiveTimeout = timeout ?? DefaultTimeout;
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < effectiveTimeout)
        {
            if (condition())
                return;

            await Task.Delay(interval ?? DefaultInterval, cancellationToken);
        }

        // One last look: the final Delay may have straddled the deadline.
        if (condition())
            return;

        throw new TimeoutException(
            $"Timed out waiting for {description} after {sw.Elapsed} (limit {effectiveTimeout}).");
    }

    /// <summary>
    /// Polls <paramref name="probe"/> until it returns a value satisfying <paramref name="predicate"/>,
    /// and returns that value.
    /// </summary>
    /// <param name="describeLast">
    /// Renders the last observed value into the timeout message. Without it a failure says only
    /// that the predicate was never met — with it, the message carries the state the value was
    /// actually stuck in, which is usually the answer.
    /// </param>
    /// <exception cref="TimeoutException">No observed value satisfied the predicate in time.</exception>
    public static async Task<T> ForAsync<T>(
        Func<Task<T?>> probe,
        Func<T, bool> predicate,
        string description,
        Func<T?, string>? describeLast = null,
        TimeSpan? timeout = null,
        TimeSpan? interval = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(predicate);

        var effectiveTimeout = timeout ?? DefaultTimeout;
        var sw = Stopwatch.StartNew();
        T? last = default;

        while (sw.Elapsed < effectiveTimeout)
        {
            last = await probe();
            if (last is not null && predicate(last))
                return last;

            await Task.Delay(interval ?? DefaultInterval, cancellationToken);
        }

        var lastDescription = describeLast is not null
            ? $" Last observed: {describeLast(last)}."
            : string.Empty;

        throw new TimeoutException(
            $"Timed out waiting for {description} after {sw.Elapsed} (limit {effectiveTimeout}).{lastDescription}");
    }
}
