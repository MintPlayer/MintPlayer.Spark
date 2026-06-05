namespace MintPlayer.Spark.Cron;

/// <summary>
/// A background job that runs on a cron schedule. Implement this interface and declare
/// the schedule via the <see cref="CronSchedule"/> static abstract member; the schedule
/// is read once at registration time (no instance is created to read it).
/// </summary>
/// <remarks>
/// Jobs are resolved from a fresh DI scope for every run, so constructor/<c>[Inject]</c>
/// dependencies (e.g. <c>IAsyncDocumentSession</c>) work exactly as in any scoped service.
/// </remarks>
public interface ISparkCronJob
{
    /// <summary>
    /// The cron expression (NCrontab syntax) that determines when this job runs.
    /// Five fields = minute precision; six fields = include seconds. Always interpreted in UTC.
    /// </summary>
    static abstract string CronSchedule { get; }

    /// <summary>
    /// When <see langword="false"/> (the default), a still-running occurrence suppresses the
    /// next one on the same node — a run that overruns its interval simply skips intervening
    /// occurrences. When <see langword="true"/>, occurrences fire independently and may overlap.
    /// </summary>
    static virtual bool AllowConcurrentRuns => false;

    /// <summary>Executes the job. Throwing is logged and does not stop the schedule.</summary>
    Task RunAsync(CancellationToken cancellationToken);
}
