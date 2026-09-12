using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;

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
    /// The viewer's timezone as declared by the <c>X-Spark-Timezone</c> header, or
    /// <see cref="TimeZoneInfo.Utc"/> when the header is absent, unparseable, or names an unknown zone.
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
    /// The header the Spark client sends on every request, carrying an IANA zone id such as
    /// <c>Europe/Brussels</c>. Must stay in lockstep with <c>spark-timezone.interceptor.ts</c>.
    /// </summary>
    public const string HeaderName = "X-Spark-Timezone";

    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly ILogger<RequestTimeZoneResolver>? logger;

    public TimeZoneInfo GetViewerTimeZone()
    {
        var header = httpContextAccessor.HttpContext?.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(header))
            return TimeZoneInfo.Utc;

        return TryResolve(header.Trim(), out var zone)
            ? zone
            : TimeZoneInfo.Utc;
    }

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

    private bool TryResolve(string id, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // The hazard on a container is not missing tzdata -- every supported base image ships a
            // current one -- but zone RENAMES: a browser on a newer tzdata sends an id the image has
            // never heard of, or an older browser sends a retired one such as America/Godthab.
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId))
            {
                try
                {
                    zone = TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                    return true;
                }
                catch (Exception inner) when (inner is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                }
            }

            logger?.LogWarning(
                "Unknown timezone {TimeZoneId} in the {Header} header; falling back to UTC. " +
                "This is usually a zone rename between the browser's tzdata and the server's.",
                id, HeaderName);

            zone = null;
            return false;
        }
    }
}
