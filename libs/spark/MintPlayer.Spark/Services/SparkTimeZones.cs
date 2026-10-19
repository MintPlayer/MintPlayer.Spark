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
