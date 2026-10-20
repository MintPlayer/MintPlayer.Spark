namespace MintPlayer.Spark.Services;

/// <summary>
/// Time-zone lookup and conversion for code with no request in hand: schedulers, cron slots, reports
/// (K10). Libraries and applications never call <see cref="TimeZoneInfo.FindSystemTimeZoneById"/> or
/// <c>TimeZoneInfo.ConvertTime*</c> themselves; those are banned outside the core (<c>BannedSymbols.TimeZones.txt</c>),
/// and this class is what the ban message points to.
/// </summary>
/// <remarks>
/// Lookup goes through the same validation and alias table as <see cref="IRequestTimeZoneResolver"/>:
/// an IANA-shaped id of at most 64 characters, its Windows equivalent, then the zones a newer browser
/// reports that this server's tzdata may not know. Text for people goes through
/// <see cref="Abstractions.DateTimes.ISparkDateTimeFormatter"/>, not through these.
/// </remarks>
public static class SparkTimeZones
{
    /// <summary>The zone named by an IANA id, or <see langword="null"/> when it is malformed or unknown.</summary>
    public static TimeZoneInfo? Find(string? ianaZoneId)
    {
        var id = ianaZoneId?.Trim();
        if (string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase) || string.Equals(id, "Etc/UTC", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Utc;
        if (!RequestTimeZoneResolver.IsWellFormedZoneId(id))
            return null;
        return RequestTimeZoneResolver.Find(id!, out var zone, out _) == RequestTimeZoneResolver.ZoneMatch.None ? null : zone;
    }

    /// <summary>The zone named by an IANA id; UTC when it is empty, malformed or unknown.</summary>
    public static TimeZoneInfo FindOrUtc(string? ianaZoneId) => Find(ianaZoneId) ?? TimeZoneInfo.Utc;

    /// <summary>Whether <paramref name="ianaZoneId"/> names a zone this server knows.</summary>
    public static bool IsKnown(string? ianaZoneId) => Find(ianaZoneId) is not null;

    /// <summary>The same instant as a wall clock and offset in <paramref name="zone"/>.</summary>
    public static DateTimeOffset ToZone(DateTimeOffset instant, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(instant, zone);

    /// <summary>
    /// The instant a wall clock in <paramref name="zone"/> names, choosing what a browser chooses across a
    /// DST discontinuity: the daylight offset in the autumn fold, the pre-transition offset in the spring gap
    /// (never throws, unlike <c>TimeZoneInfo.ConvertTimeToUtc</c>). A <see cref="DateTime"/> with a kind is
    /// taken as the instant it already is.
    /// </summary>
    public static DateTimeOffset ToInstant(DateTime wallClock, TimeZoneInfo zone) => RequestTimeZoneResolver.ToInstant(wallClock, zone, logger: null);

    /// <summary>
    /// The first instant strictly after <paramref name="after"/> at which a wall-clock schedule fires in
    /// <paramref name="zone"/> (warehouse WX14, Q-W1). <paramref name="nextWallClockAfter"/> is the schedule
    /// itself, zone-free: given a wall clock it returns the schedule's next wall clock strictly after it, or
    /// <see langword="null"/> when there is none (a cron expression evaluated as if in UTC is exactly that).
    /// Returns <see langword="null"/> when the schedule has no further occurrence.
    /// </summary>
    /// <remarks>
    /// The two DST discontinuities are defined, and differ on purpose from <see cref="ToInstant"/> (which
    /// follows the browser for values a person typed):
    /// <list type="bullet">
    /// <item><b>Gap</b> (spring forward): a wall clock that never happens fires at the <b>first valid instant
    /// after the gap</b>, the transition itself. Brussels 02:30 on the spring-forward day fires at 03:00 CEST
    /// (01:00Z). Several skipped wall clocks (every 15 minutes, say) collapse into that one instant.</item>
    /// <item><b>Fold</b> (fall back): a wall clock that happens twice fires <b>once</b>, at its first
    /// occurrence (the larger, pre-transition offset). New York 01:30 on the fall-back day fires at 01:30 EDT
    /// (05:30Z), not again at 01:30 EST.</item>
    /// </list>
    /// The result is a UTC instant, so it is unambiguous across a fold and strictly increases from call to
    /// call: a caller that keys runs on it (the cron scheduler's cluster-wide claim) needs nothing else.
    /// </remarks>
    public static DateTimeOffset? NextWallClockOccurrence(DateTimeOffset after, TimeZoneInfo zone, Func<DateTime, DateTime?> nextWallClockAfter)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(nextWallClockAfter);

        var afterUtc = after.UtcDateTime;
        var cursor = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(after, zone).DateTime, DateTimeKind.Unspecified);

        // Bounded: a fold is at most a couple of hours, so even a per-second schedule skips a few thousand
        // first-pass wall clocks before it leaves it. The cap only stops a misbehaving delegate.
        for (var i = 0; i < 1_000_000; i++)
        {
            var next = nextWallClockAfter(cursor);
            if (next is not { } candidate)
                return null;
            candidate = DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified);
            if (candidate <= cursor)
                return null; // a delegate that does not move forward would loop forever

            var instant = WallClockFiringInstant(candidate, zone);
            if (instant.UtcDateTime > afterUtc)
                return instant;

            // A fold's second pass (its first-pass instant already lies behind us), or a gap whose
            // transition instant already fired: move on to the next wall clock.
            cursor = candidate;
        }

        return null;
    }

    /// <summary>The instant a scheduled wall clock fires at, with the gap and fold rules of <see cref="NextWallClockOccurrence"/>.</summary>
    private static DateTimeOffset WallClockFiringInstant(DateTime wallClock, TimeZoneInfo zone)
    {
        if (zone.IsAmbiguousTime(wallClock))
        {
            var first = zone.GetAmbiguousTimeOffsets(wallClock).Max();
            return new DateTimeOffset(wallClock - first, TimeSpan.Zero);
        }

        if (zone.IsInvalidTime(wallClock))
            return new DateTimeOffset(GapEndUtc(wallClock, zone), TimeSpan.Zero);

        return new DateTimeOffset(wallClock - zone.GetUtcOffset(wallClock), TimeSpan.Zero);
    }

    /// <summary>
    /// The transition instant that ends the gap <paramref name="wallClock"/> falls in: the smallest UTC
    /// instant whose local wall clock lies past it. Found by bisection over the instants, since the local
    /// wall clock only moves forward outside a fold, and offsets lie within -12h..+14h.
    /// </summary>
    private static DateTime GapEndUtc(DateTime wallClock, TimeZoneInfo zone)
    {
        var lo = DateTime.SpecifyKind(wallClock, DateTimeKind.Utc).AddHours(-16).Ticks; // local < wallClock
        var hi = DateTime.SpecifyKind(wallClock, DateTimeKind.Utc).AddHours(16).Ticks;  // local > wallClock
        while (hi - lo > 1)
        {
            var mid = lo + (hi - lo) / 2;
            var utc = new DateTime(mid, DateTimeKind.Utc);
            if (utc + zone.GetUtcOffset(utc) > wallClock)
                hi = mid;
            else
                lo = mid;
        }
        return new DateTime(hi, DateTimeKind.Utc);
    }

    /// <summary>
    /// The English city an IANA id names, the last step of every zone-label fallback (D36):
    /// <c>Europe/Brussels</c> → <c>Brussels</c>, <c>America/Argentina/Buenos_Aires</c> → <c>Buenos Aires</c>,
    /// <c>UTC</c> → <c>UTC</c>. Must stay in lockstep with <c>sparkTimeZoneCity</c> in ng-spark's date-time entry point.
    /// </summary>
    public static string CityFromId(string ianaZoneId)
    {
        var id = (ianaZoneId ?? string.Empty).Trim();
        if (id.Length == 0)
            return "UTC";
        var slash = id.LastIndexOf('/');
        var city = slash >= 0 ? id[(slash + 1)..] : id;
        return city.Length == 0 ? id : city.Replace('_', ' ');
    }
}
