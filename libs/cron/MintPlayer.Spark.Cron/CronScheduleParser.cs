using NCrontab;

namespace MintPlayer.Spark.Cron;

/// <summary>
/// Centralizes NCrontab parsing so registration-time validation (<see cref="ISparkCronBuilder"/>)
/// and the runtime scheduler (<see cref="SparkCronScheduler"/>) agree on field-count precision:
/// five fields = minute precision, six fields = seconds. Always interpreted in UTC.
/// </summary>
internal static class CronScheduleParser
{
    /// <summary>Six space-separated fields means the expression includes a leading seconds field.</summary>
    public static bool IncludesSeconds(string cronSchedule)
        => cronSchedule.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6;

    /// <summary>
    /// Tries to parse <paramref name="cronSchedule"/>. Returns <see langword="false"/> for a
    /// syntactically-invalid expression (rather than throwing), so callers can fail fast at
    /// registration with a clear message. A syntactically-valid but never-occurring expression
    /// (e.g. <c>0 0 30 2 *</c>) still parses here — the "no future occurrence" case is detected by
    /// the scheduler via <see cref="NCrontab.CrontabSchedule.GetNextOccurrence(System.DateTime)"/>.
    /// </summary>
    public static bool TryParse(string cronSchedule, out CrontabSchedule schedule, out bool includesSeconds)
    {
        includesSeconds = IncludesSeconds(cronSchedule);
        var parsed = CrontabSchedule.TryParse(
            cronSchedule,
            new CrontabSchedule.ParseOptions { IncludingSeconds = includesSeconds });
        schedule = parsed!;
        return parsed is not null;
    }
}
