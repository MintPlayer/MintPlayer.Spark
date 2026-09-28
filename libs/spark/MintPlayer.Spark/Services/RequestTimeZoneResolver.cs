using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Configuration;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Resolves the timezone of the browser that made the current request, and converts wall-clock
/// values into instants the way that browser would have.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is for server-initiated work only.</b> A <see cref="DateTimeOffset"/> in Spark means an
/// <i>instant</i>; on the ordinary read and write paths the browser does the converting in both
/// directions and the server never reconstructs an instant from a wall clock. A container's timezone
/// rules are frozen at image build time while a browser's are kept fresh by the operating system, so
/// a stale image that converted on the server would store a permanently wrong instant.
/// </para>
/// <para>
/// The resolver exists for the cases where there is no browser in the loop and the server still has
/// to produce a local time — a scheduled report, a nightly export, an email that says "your appointment
/// is at 09:00". For those, <see cref="ToViewerDateTimeOffset(DateTime)"/> reproduces the choice the
/// browser would have made, so the two paths cannot drift apart.
/// </para>
/// </remarks>
public interface IRequestTimeZoneResolver
{
    /// <summary>
    /// The viewer's timezone: the <c>X-Spark-Timezone</c> header when it names a valid zone, else the
    /// <c>spark-timezone</c> cookie (<see cref="Configuration.SparkTimeZoneOptions.CookieName"/>) when
    /// it does, else <see cref="TimeZoneInfo.Utc"/>. Outside a request (a background job) it is UTC.
    /// </summary>
    TimeZoneInfo GetViewerTimeZone();

    /// <summary>
    /// Interprets <paramref name="wallClock"/> as a local time in the viewer's zone and returns the
    /// instant it names, choosing the same instant the browser would choose across a DST discontinuity.
    /// </summary>
    DateTimeOffset ToViewerDateTimeOffset(DateTime wallClock);

    /// <inheritdoc cref="ToViewerDateTimeOffset(DateTime)"/>
    DateTimeOffset ToViewerDateTimeOffset(DateTime wallClock, TimeZoneInfo zone);
}

[Register(typeof(IRequestTimeZoneResolver), ServiceLifetime.Scoped)]
internal partial class RequestTimeZoneResolver : IRequestTimeZoneResolver
{
    /// <summary>
    /// The header the Spark client sends on every browser request, carrying an IANA zone id such as
    /// <c>Europe/Brussels</c>. Must stay in lockstep with <c>spark-timezone.interceptor.ts</c>.
    /// </summary>
    public const string HeaderName = "X-Spark-Timezone";

    /// <summary>The longest id accepted from a header or cookie (the longest id a browser reports is 30, S-TZ4).</summary>
    internal const int MaxIdLength = 64;

    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly IOptions<SparkTimeZoneOptions> options;
    [Inject] private readonly ILogger<RequestTimeZoneResolver>? logger;

    // Scoped, so one resolution (and one log line) per request.
    private TimeZoneInfo? resolved;

    /// <summary>
    /// An IANA-shaped zone id: a letter, then up to three <c>/</c>-separated segments of letters,
    /// digits, <c>_</c>, <c>+</c> and <c>-</c>. No dots, spaces, backslashes or empty segments, so
    /// nothing path-like reaches <see cref="TimeZoneInfo.FindSystemTimeZoneById"/> (S-TZ1: on Linux
    /// that call reads a file under <c>/usr/share/zoneinfo</c>, and resolves <c>Europe//Brussels</c>).
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_+\-]*(/[A-Za-z0-9_+\-]+){0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex ZoneIdShape();

    /// <summary>
    /// Whether <paramref name="id"/> passes the validation applied before any lookup: at most
    /// <see cref="MaxIdLength"/> characters and IANA-shaped (<see cref="ZoneIdShape"/>).
    /// </summary>
    internal static bool IsWellFormedZoneId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= MaxIdLength && ZoneIdShape().IsMatch(id);

    public TimeZoneInfo GetViewerTimeZone()
    {
        if (resolved is not null)
            return resolved;

        var request = httpContextAccessor.HttpContext?.Request;
        if (request is null)
            return TimeZoneInfo.Utc;

        return resolved = Resolve(request);
    }

    private TimeZoneInfo Resolve(HttpRequest request)
    {
        // A repeated header joins with a comma, which fails the shape check and falls through.
        if (TryResolve(request.Headers[HeaderName].ToString(), HeaderSource, out var zone))
            return zone;

        var cookieName = options.Value.CookieName;
        if (!string.IsNullOrWhiteSpace(cookieName)
            && TryResolve(request.Cookies[cookieName], CookieSource, out zone))
            return zone;

        logger?.LogDebug("Viewer timezone: UTC (no valid {Header} header or timezone cookie).", HeaderName);
        return TimeZoneInfo.Utc;
    }

    private const string HeaderSource = "header";
    private const string CookieSource = "cookie";

    public DateTimeOffset ToViewerDateTimeOffset(DateTime wallClock)
        => ToViewerDateTimeOffset(wallClock, GetViewerTimeZone());

    public DateTimeOffset ToViewerDateTimeOffset(DateTime wallClock, TimeZoneInfo zone)
    {
        // A DateTime that already carries a kind is not a wall clock in the viewer's zone, so honour it
        // rather than reinterpreting it.
        if (wallClock.Kind != DateTimeKind.Unspecified)
            return new DateTimeOffset(wallClock.ToUniversalTime(), TimeSpan.Zero);

        // Deliberately GetUtcOffset and never ConvertTimeToUtc: the latter THROWS on a wall clock inside
        // the spring gap, and this value can come straight from something a person typed.
        var offset = zone.GetUtcOffset(wallClock);

        if (zone.IsAmbiguousTime(wallClock))
        {
            // The autumn fold: this wall clock happens twice, and the two candidates are an hour apart.
            // GetUtcOffset picks the STANDARD one; a browser picks the DAYLIGHT one. Measured 2026-09-12
            // across Europe/Brussels, Australia/Sydney, America/Santiago and America/New_York -- the
            // browser took the larger offset in every case, which is the ECMAScript rule (the offset in
            // force BEFORE a fall-back). Match it, so a value converted here and the same value converted
            // in the browser name the same instant.
            offset = zone.GetAmbiguousTimeOffsets(wallClock).Max();

            logger?.LogDebug(
                "Ambiguous wall clock {WallClock:o} in {Zone}; chose {Offset} to match the browser.",
                wallClock, zone.Id, offset);
        }
        else if (zone.IsInvalidTime(wallClock))
        {
            // The spring gap: this wall clock never happens. GetUtcOffset returns the pre-transition
            // offset, which yields the same INSTANT the browser produces by shifting the clock forward
            // past the gap -- measured identical, so there is nothing to correct here. Only the offset
            // label differs, and the instant is what we store.
            logger?.LogDebug(
                "Invalid wall clock {WallClock:o} in {Zone} (inside the spring-forward gap); resolving to {Offset}.",
                wallClock, zone.Id, offset);
        }

        return new DateTimeOffset(wallClock, offset);
    }

    private bool TryResolve(string? raw, string source, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        zone = null;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var id = raw.Trim();
        if (!IsWellFormedZoneId(id))
        {
            // Deliberately not logged verbatim: the value is whatever the client sent.
            logger?.LogDebug(
                "Ignored a malformed timezone id in the {Source} ({Length} characters).", source, id.Length);
            return false;
        }

        var match = Find(id, out var found, out var via);
        if (match == ZoneMatch.None || found is null)
        {
            // Safe to log: it passed the shape check (IANA characters only, at most 64).
            logger?.LogWarning(
                "Unknown timezone {TimeZoneId} in the {Source}; trying the next source, then UTC. " +
                "This is usually a zone rename between the browser's tzdata and the server's.",
                id, source);
            return false;
        }

        zone = found;
        switch (match)
        {
            case ZoneMatch.Direct:
                logger?.LogDebug("Viewer timezone {TimeZoneId} from the {Source}.", zone.Id, source);
                break;
            case ZoneMatch.WindowsId:
                logger?.LogDebug("Viewer timezone {TimeZoneId} (as {WindowsId}) from the {Source}.", id, via, source);
                break;
            default:
                // Safe to log: it passed the shape check, and the alias is one of ours.
                logger?.LogInformation(
                    "Viewer timezone {TimeZoneId} from the {Source} is unknown to this server's timezone data; " +
                    "using {Alias}, which has the same current rules.", id, source, via);
                break;
        }
        return true;
    }

    internal enum ZoneMatch { None, Direct, WindowsId, Alias }

    /// <summary>
    /// Zones a browser reports that this server's timezone data may not know, each mapped to a zone with
    /// the SAME CURRENT rules (offset and DST schedule from 2026 on; history differs, and only the
    /// present matters for a server-initiated local time). Consulted only when the id itself does not
    /// resolve, so a server whose tzdata knows the real zone uses it.
    /// </summary>
    /// <remarks>
    /// Measured 2026-09-28 on Windows 11 (.NET 11 rc.1, Windows ICU 72.1): all five below fail both
    /// <see cref="TimeZoneInfo.FindSystemTimeZoneById"/> and <see cref="TimeZoneInfo.TryConvertIanaIdToWindowsId(string, out string?)"/>.
    /// Deliberately NOT aliased through the Windows zone CLDR names for them (Central Asia Standard Time
    /// for Urumqi and Vostok): on that machine Central Asia Standard Time still says +06 for Almaty, which
    /// moved to +05 in 2024, so the Windows zone is itself stale.
    /// </remarks>
    internal static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // US Mountain time with US DST since 2022-11-30 (tzdata 2022g), like Denver.
        ["America/Ciudad_Juarez"] = "America/Denver",
        // Aysén: permanent -03 since 2025 (tzdata 2025b), like Magallanes since 2016.
        ["America/Coyhaique"] = "America/Punta_Arenas",
        // Permanent +05 since 2023-12-18 (tzdata 2024a), like Uzbekistan.
        ["Antarctica/Vostok"] = "Asia/Tashkent",
        // Xinjiang time: permanent +06, like Bangladesh (no DST since 2010).
        ["Asia/Urumqi"] = "Asia/Dhaka",
    };

    /// <summary>
    /// <c>Antarctica/Troll</c>: +00, and +02 from the last Sunday of March to the last Sunday of October,
    /// both switches at 01:00 UTC (the EU instants). No system zone has a two-hour DST step, so it is
    /// built from the tzdata rule rather than aliased.
    /// </summary>
    internal static readonly Lazy<TimeZoneInfo> Troll = new(() => TimeZoneInfo.CreateCustomTimeZone(
        "Antarctica/Troll", TimeSpan.Zero, "(UTC+00:00/+02:00) Troll", "+00", "+02",
        [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(2),
            // Start in standard local time (01:00+00 = 01:00Z), end in daylight local time (03:00+02 = 01:00Z).
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday))]));

    /// <summary>
    /// Looks <paramref name="id"/> up: the system zone, else its Windows equivalent, else an
    /// <see cref="Aliases"/> entry (or <see cref="Troll"/>). <paramref name="via"/> is the Windows id or
    /// the alias used.
    /// </summary>
    internal static ZoneMatch Find(string id, out TimeZoneInfo? zone, out string? via)
    {
        via = null;
        if (TryFindSystemZone(id, out zone, out var windowsId))
        {
            via = windowsId;
            return windowsId is null ? ZoneMatch.Direct : ZoneMatch.WindowsId;
        }

        // The hazard on a container is not missing tzdata -- every supported base image ships a current
        // one -- but zone RENAMES and NEW zones: a browser on a newer tzdata sends an id the server has
        // never heard of, or an older browser sends a retired one such as America/Godthab.
        if (string.Equals(id, "Antarctica/Troll", StringComparison.OrdinalIgnoreCase))
        {
            zone = Troll.Value;
            via = "the built-in Antarctica/Troll rule";
            return ZoneMatch.Alias;
        }
        if (Aliases.TryGetValue(id, out var alias) && TryFindSystemZone(alias, out zone, out _))
        {
            via = alias;
            return ZoneMatch.Alias;
        }

        zone = null;
        return ZoneMatch.None;
    }

    private static bool TryFindSystemZone(string id, [NotNullWhen(true)] out TimeZoneInfo? zone, out string? windowsId)
    {
        windowsId = null;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var converted))
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(converted);
                windowsId = converted;
                return true;
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }

        zone = null;
        return false;
    }
}
