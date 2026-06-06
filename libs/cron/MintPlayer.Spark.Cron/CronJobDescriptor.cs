namespace MintPlayer.Spark.Cron;

/// <summary>
/// Immutable registration record for a cron job: the concrete type plus the schedule
/// metadata captured (strongly typed) from the job's static abstract members.
/// </summary>
public sealed record CronJobDescriptor(
    Type JobType,
    string Name,
    string CronSchedule,
    bool AllowConcurrentRuns);
