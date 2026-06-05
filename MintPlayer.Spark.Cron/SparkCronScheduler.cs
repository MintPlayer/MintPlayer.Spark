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
        CrontabSchedule schedule;
        try
        {
            var fieldCount = descriptor.CronSchedule
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

            schedule = CrontabSchedule.Parse(
                descriptor.CronSchedule,
                new CrontabSchedule.ParseOptions { IncludingSeconds = fieldCount == 6 });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Invalid cron expression '{Cron}' for job '{Job}'; the job will not run.",
                descriptor.CronSchedule, descriptor.Name);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowUtc = DateTime.UtcNow;
            var nextUtc = DateTime.SpecifyKind(schedule.GetNextOccurrence(nowUtc), DateTimeKind.Utc);

            var delay = nextUtc - nowUtc;
            if (delay > TimeSpan.Zero)
            {
                if (!await SafeDelayAsync(delay < MaxSleep ? delay : MaxSleep, stoppingToken))
                    return;

                // Woke early only to re-evaluate a long wait — recompute before firing.
                if (delay >= MaxSleep)
                    continue;
            }

            await TryRunOnceAsync(descriptor, nextUtc, stoppingToken);
        }
    }

    private async Task TryRunOnceAsync(CronJobDescriptor descriptor, DateTime occurrenceUtc, CancellationToken stoppingToken)
    {
        bool claimed;
        try
        {
            claimed = await TryClaimOccurrenceAsync(descriptor.Name, occurrenceUtc, stoppingToken);
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

        var run = ExecuteJobAsync(descriptor, stoppingToken);

        // Non-concurrent (default): await, so an overrunning run naturally suppresses overlap.
        // Concurrent: fire-and-forget so the loop can schedule the next occurrence immediately.
        if (!descriptor.AllowConcurrentRuns)
            await run;
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

    /// <summary>
    /// Claims a single occurrence cluster-wide using one compare-exchange key per job. The key's
    /// value is the latest claimed occurrence (ISO-8601 UTC, which sorts lexically), so this both
    /// dedups the current occurrence and rejects stale ones. Returns <see langword="true"/> only
    /// for the node that wins the atomic compare-and-swap.
    /// </summary>
    private async Task<bool> TryClaimOccurrenceAsync(string jobName, DateTime occurrenceUtc, CancellationToken stoppingToken)
    {
        var key = $"cron/{jobName}";
        var occurrence = occurrenceUtc.ToString("O");

        var current = await documentStore.Operations.SendAsync(
            new GetCompareExchangeValueOperation<string>(key), token: stoppingToken);

        if (current?.Value is { Length: > 0 } existing && string.CompareOrdinal(existing, occurrence) >= 0)
            return false; // this occurrence (or a later one) already claimed

        var result = await documentStore.Operations.SendAsync(
            new PutCompareExchangeValueOperation<string>(key, occurrence, current?.Index ?? 0),
            token: stoppingToken);

        return result.Successful; // false if another node won the race
    }

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
