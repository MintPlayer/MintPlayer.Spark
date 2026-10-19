using MintPlayer.Spark.Services;
using NCrontab;

namespace MintPlayer.Spark.Cron;

/// <summary>
/// Centralizes NCrontab parsing so registration-time validation (<see cref="ISparkCronBuilder"/>)
/// and the runtime scheduler (<see cref="SparkCronScheduler"/>) agree on field-count precision:
/// five fields = minute precision, six fields = seconds. Also owns the next-occurrence computation:
/// in UTC by default, or on a time zone's local wall clock through the core's
/// <see cref="SparkTimeZones.NextWallClockOccurrence"/> (warehouse WX14, K10).
/// </summary>
internal static class CronScheduleParser
{
    /// <summary>
    /// A computed next occurrence at or beyond this point is treated as "no future occurrence"
    /// (NCrontab returns the end sentinel for never-occurring expressions like 30 February).
    /// </summary>
    private static readonly DateTime NoOccurrenceThreshold = DateTime.MaxValue.AddDays(-2);

    /// <summary>Six space-separated fields means the expression includes a leading seconds field.</summary>
    public static bool IncludesSeconds(string cronSchedule)
        => cronSchedule.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6;

    /// <summary>
    /// Tries to parse <paramref name="cronSchedule"/>. Returns <see langword="false"/> for a
    /// syntactically-invalid expression (rather than throwing), so callers can fail fast at
    /// registration with a clear message. A syntactically-valid but never-occurring expression
    /// (e.g. <c>0 0 30 2 *</c>) still parses here — the "no future occurrence" case is detected by
    /// the scheduler via <see cref="NextOccurrenceUtc"/>.
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

    /// <summary>
    /// Resolves a schedule's time zone: <see langword="null"/> or empty means UTC (<paramref name="zone"/>
    /// is then <see langword="null"/>, the unchanged UTC path); otherwise an IANA id the server knows.
    /// Returns <see langword="false"/> for a malformed or unknown id.
    /// </summary>
    public static bool TryResolveTimeZone(string? timeZone, out TimeZoneInfo? zone)
    {
        zone = null;
        if (string.IsNullOrWhiteSpace(timeZone))
            return true;
        var found = SparkTimeZones.Find(timeZone);
        if (found is null)
            return false;
        // "UTC" named explicitly takes the same path as no zone at all.
        zone = ReferenceEquals(found, TimeZoneInfo.Utc) ? null : found;
        return true;
    }

    /// <summary>
    /// The next occurrence strictly after <paramref name="fromUtc"/>, as a UTC instant, or
    /// <see langword="null"/> when the expression has no future occurrence. Without a zone the expression
    /// is evaluated in UTC exactly as before WX14. With one it is evaluated on that zone's local wall
    /// clock: a wall clock DST skips fires at the first valid instant after the gap, one DST repeats
    /// fires once (its first occurrence). The result is always a UTC instant, so it is unambiguous
    /// across a fold and the cluster-wide claim keyed on it stays one-run-per-occurrence.
    /// </summary>
    public static DateTime? NextOccurrenceUtc(CrontabSchedule schedule, TimeZoneInfo? zone, DateTime fromUtc)
    {
        if (zone is null)
        {
            var next = NextWallClock(schedule, fromUtc);
            return next is null ? null : DateTime.SpecifyKind(next.Value, DateTimeKind.Utc);
        }

        var instant = SparkTimeZones.NextWallClockOccurrence(
            new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)),
            zone,
            wallClock => NextWallClock(schedule, wallClock));
        return instant?.UtcDateTime;
    }

    /// <summary>The schedule's next wall clock strictly after <paramref name="from"/>, zone-free.</summary>
    private static DateTime? NextWallClock(CrontabSchedule schedule, DateTime from)
    {
        DateTime next;
        try
        {
            next = schedule.GetNextOccurrence(from);
        }
        catch
        {
            return null;
        }

        // NCrontab returns its end sentinel (≈ DateTime.MaxValue) when no occurrence exists.
        return next >= NoOccurrenceThreshold ? null : next;
    }
}
