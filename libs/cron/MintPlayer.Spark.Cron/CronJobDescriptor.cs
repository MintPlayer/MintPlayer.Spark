namespace MintPlayer.Spark.Cron;

/// <summary>
/// Immutable registration record for a cron job: the concrete type plus the schedule
/// metadata captured (strongly typed) from the job's static abstract members.
/// </summary>
/// <param name="TimeZone">
/// The IANA zone whose local wall clock <paramref name="CronSchedule"/> is evaluated on, or
/// <see langword="null"/> for UTC (the default). A time DST skips fires at the first valid instant after
/// the gap; a time DST repeats fires once (warehouse WX14, Q-W1).
/// </param>
public sealed record CronJobDescriptor(
    Type JobType,
    string Name,
    string CronSchedule,
    bool AllowConcurrentRuns,
    string? TimeZone = null);
