using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using NCrontab;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;

namespace MintPlayer.Spark.Cron;

/// <summary>
/// Hosted service that runs one loop per registered cron job. Each loop computes the next
/// occurrence (in UTC) via NCrontab, waits for it, and runs the job — but only after winning
/// a cluster-wide RavenDB compare-exchange claim for that occurrence, so a job fires exactly
/// once across all nodes.
/// </summary>
internal sealed partial class SparkCronScheduler : BackgroundService
{
    [Inject] private readonly SparkCronJobRegistry registry;
    [Inject] private readonly IServiceProvider serviceProvider;
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ILoggerFactory loggerFactory;

    private ILogger logger = null!;

    /// <summary>Cap on a single wait so long delays and host clock corrections are re-evaluated.</summary>
    private static readonly TimeSpan MaxSleep = TimeSpan.FromHours(1);

    /// <summary>
    /// A computed next occurrence at or beyond this point is treated as "no future occurrence"
    /// (NCrontab returns the end sentinel for never-occurring expressions like 30 February).
    /// </summary>
    private static readonly DateTime NoOccurrenceThreshold = DateTime.MaxValue.AddDays(-2);

    /// <summary>
    /// Clock-skew tolerance for the stored claim value. A legitimate claim value can never be more
    /// than this far in the future (a node only claims an occurrence once its own clock reaches it),
    /// so a value beyond this window is treated as poison rather than a valid prior claim.
    /// </summary>
    private static readonly TimeSpan MaxClaimFutureSkew = TimeSpan.FromDays(1);

    /// <summary>Upper bound on simultaneously-running occurrences of a single concurrent job, so an
    /// overrunning job cannot exhaust the host with unbounded fan-out.</summary>
    internal const int MaxConcurrentRunsPerJob = 10;

    [PostConstruct]
    private void InitializeLogger() => logger = loggerFactory.CreateLogger<SparkCronScheduler>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var jobs = registry.Jobs;
        if (jobs.Count == 0)
        {
            logger.LogInformation("No cron jobs registered; SparkCronScheduler will not run any loops.");
            return;
        }

        logger.LogInformation("SparkCronScheduler starting {Count} cron job loop(s): {Jobs}",
            jobs.Count, string.Join(", ", jobs.Select(j => j.Name)));

        await Task.WhenAll(jobs.Select(j => RunJobLoopAsync(j, stoppingToken)));
    }

    private async Task RunJobLoopAsync(CronJobDescriptor descriptor, CancellationToken stoppingToken)
    {
        if (!CronScheduleParser.TryParse(descriptor.CronSchedule, out var schedule, out var includesSeconds))
        {
            // Unreachable via AddJob (which validates), but defends a hand-built descriptor.
            logger.LogWarning("Invalid cron expression '{Cron}' for job '{Job}'; the job will not run.",
                descriptor.CronSchedule, descriptor.Name);
            return;
        }

        var firstOccurrence = NextOccurrenceUtc(schedule, DateTime.UtcNow);
        if (firstOccurrence is null)
        {
            // Syntactically valid but never occurs (e.g. "0 0 30 2 *"). Don't enter an indefinite
            // re-sleep loop that never fires — surface it and disable only this loop.
            logger.LogWarning(
                "Cron expression '{Cron}' ({Precision}) for job '{Job}' has no future occurrence; the job will not run.",
                descriptor.CronSchedule, includesSeconds ? "second precision" : "minute precision", descriptor.Name);
            return;
        }

        logger.LogInformation(
            "Cron job '{Job}' scheduled with {Precision} expression '{Cron}'; first run at {Next:o} (UTC).",
            descriptor.Name, includesSeconds ? "second-precision" : "minute-precision",
            descriptor.CronSchedule, firstOccurrence.Value);

        // Concurrent jobs are dispatched fire-and-forget; bound their fan-out and track outstanding
        // runs so shutdown can drain them. Non-concurrent jobs never use these.
        using var concurrencyGate = descriptor.AllowConcurrentRuns
            ? new SemaphoreSlim(MaxConcurrentRunsPerJob, MaxConcurrentRunsPerJob)
            : null;
        var inFlight = new ConcurrentDictionary<Task, byte>();

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowUtc = DateTime.UtcNow;
            var next = NextOccurrenceUtc(schedule, nowUtc);
            if (next is null)
            {
                logger.LogWarning("Cron job '{Job}' no longer has a future occurrence; stopping its loop.",
                    descriptor.Name);
                break;
            }

            var nextUtc = next.Value;
            var delay = nextUtc - nowUtc;
            if (delay > TimeSpan.Zero)
            {
                if (!await SafeDelayAsync(delay < MaxSleep ? delay : MaxSleep, stoppingToken))
                    break;

                // Woke early only to re-evaluate a long wait — recompute before firing.
                if (delay >= MaxSleep)
                    continue;
            }

            await TryRunOnceAsync(descriptor, nextUtc, concurrencyGate, inFlight, stoppingToken);
        }

        // Graceful shutdown: let any in-flight concurrent runs finish (each swallows its own
        // exceptions, so this never faults).
        await Task.WhenAll(inFlight.Keys);
    }

    private async Task TryRunOnceAsync(CronJobDescriptor descriptor, DateTime occurrenceUtc,
        SemaphoreSlim? concurrencyGate, ConcurrentDictionary<Task, byte> inFlight, CancellationToken stoppingToken)
    {
        bool claimed;
        try
        {
            claimed = await TryClaimOccurrenceAsync(documentStore, descriptor.Name, occurrenceUtc, stoppingToken, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to claim occurrence {Occurrence:o} for cron job '{Job}'; skipping this run.",
                occurrenceUtc, descriptor.Name);
            return;
        }

        if (!claimed)
        {
            logger.LogDebug("Cron job '{Job}' occurrence {Occurrence:o} already claimed (likely another node); skipping.",
                descriptor.Name, occurrenceUtc);
            return;
        }

        // Non-concurrent (default): await, so an overrunning run naturally suppresses overlap.
        if (!descriptor.AllowConcurrentRuns)
        {
            await ExecuteJobAsync(descriptor, stoppingToken);
            return;
        }

        // Concurrent: cap the fan-out. If too many runs are already in flight, shed this occurrence
        // rather than letting an overrunning job exhaust threads/connections/memory.
        if (concurrencyGate is not null && !concurrencyGate.Wait(0, CancellationToken.None))
        {
            logger.LogWarning(
                "Cron job '{Job}' skipped occurrence {Occurrence:o}: {Max} concurrent runs already in flight.",
                descriptor.Name, occurrenceUtc, MaxConcurrentRunsPerJob);
            return;
        }

        var run = RunReleasingAsync(descriptor, concurrencyGate, stoppingToken);
        inFlight[run] = 0;
        _ = run.ContinueWith(t => inFlight.TryRemove(t, out _), TaskScheduler.Default);
    }

    private async Task RunReleasingAsync(CronJobDescriptor descriptor, SemaphoreSlim? concurrencyGate, CancellationToken stoppingToken)
    {
        try
        {
            await ExecuteJobAsync(descriptor, stoppingToken);
        }
        finally
        {
            concurrencyGate?.Release();
        }
    }

    private async Task ExecuteJobAsync(CronJobDescriptor descriptor, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var job = (ISparkCronJob)scope.ServiceProvider.GetRequiredService(descriptor.JobType);

            logger.LogInformation("Running cron job '{Job}'.", descriptor.Name);
            await job.RunAsync(stoppingToken);
            logger.LogInformation("Cron job '{Job}' completed.", descriptor.Name);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown mid-run — not an error.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cron job '{Job}' threw an exception.", descriptor.Name);
        }
    }

    /// <summary>Computes the next occurrence after <paramref name="fromUtc"/>, or
    /// <see langword="null"/> if the expression has no future occurrence.</summary>
    private static DateTime? NextOccurrenceUtc(CrontabSchedule schedule, DateTime fromUtc)
    {
        DateTime next;
        try
        {
            next = schedule.GetNextOccurrence(fromUtc);
        }
        catch
        {
            return null;
        }

        // NCrontab returns its end sentinel (≈ DateTime.MaxValue) when no occurrence exists.
        if (next >= NoOccurrenceThreshold)
            return null;

        return DateTime.SpecifyKind(next, DateTimeKind.Utc);
    }

    /// <summary>
    /// Claims a single occurrence cluster-wide using one compare-exchange key per job. The key's
    /// value is the latest claimed occurrence (ISO-8601 UTC, which sorts lexically), so this both
    /// dedups the current occurrence and rejects stale ones. The stored value is treated as
    /// untrusted: an unparseable or implausibly-far-future value (e.g. a poisoned <c>cron/{job}</c>
    /// key that would otherwise suppress the job forever) is logged and reclaimed instead of being
    /// honored. Returns <see langword="true"/> only for the node that wins the atomic compare-and-swap.
    /// </summary>
    internal static async Task<bool> TryClaimOccurrenceAsync(IDocumentStore documentStore, string jobName,
        DateTime occurrenceUtc, CancellationToken stoppingToken, ILogger? logger = null)
    {
        var key = $"cron/{jobName}";
        var occurrence = occurrenceUtc.ToString("O");

        var current = await documentStore.Operations.SendAsync(
            new GetCompareExchangeValueOperation<string>(key), token: stoppingToken);

        if (current?.Value is { Length: > 0 } existing)
        {
            // A legitimate value is a UTC timestamp no further ahead than now + skew. Anything else
            // is corruption or tampering — don't let it silently and permanently block the job.
            var poisonThreshold = Max(occurrenceUtc, DateTime.UtcNow).Add(MaxClaimFutureSkew);

            if (!TryParseClaimValue(existing, out var existingUtc))
            {
                logger?.LogWarning(
                    "Cron claim for job '{Job}' holds an unparseable value '{Value}'; treating as unclaimed and reclaiming.",
                    jobName, existing);
            }
            else if (existingUtc > poisonThreshold)
            {
                logger?.LogWarning(
                    "Cron claim for job '{Job}' holds an implausibly-far-future value {Value:o}; treating as poison and reclaiming.",
                    jobName, existingUtc);
            }
            else if (string.CompareOrdinal(existing, occurrence) >= 0)
            {
                return false; // this occurrence (or a later legitimate one) already claimed
            }
        }

        var result = await documentStore.Operations.SendAsync(
            new PutCompareExchangeValueOperation<string>(key, occurrence, current?.Index ?? 0),
            token: stoppingToken);

        return result.Successful; // false if another node won the race
    }

    private static bool TryParseClaimValue(string value, out DateTime utc)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            // Legitimate values are round-trip "O" UTC (Kind=Utc). Normalize anything else to UTC so
            // the future-skew comparison is apples-to-apples.
            utc = parsed.Kind switch
            {
                DateTimeKind.Utc => parsed,
                DateTimeKind.Local => parsed.ToUniversalTime(),
                _ => DateTime.SpecifyKind(parsed, DateTimeKind.Utc),
            };
            return true;
        }

        utc = default;
        return false;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static async Task<bool> SafeDelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
