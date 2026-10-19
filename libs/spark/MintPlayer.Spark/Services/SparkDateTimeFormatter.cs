using System.Globalization;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.DateTimes;

namespace MintPlayer.Spark.Services;

/// <summary>
/// <see cref="ISparkDateTimeFormatter"/>: the server twin of ng-spark's <c>&lt;spark-date-time&gt;</c> (X16).
/// The output shapes match the component's: <c>{main}</c>, <c>{main} ({hint})</c>; a rendering in a named zone
/// carries the zone's label (<c>20:00 London</c>), a stored one its offset (<c>10:00 UTC+02:00</c>).
/// </summary>
[Register(typeof(ISparkDateTimeFormatter), ServiceLifetime.Singleton)]
internal partial class SparkDateTimeFormatter : ISparkDateTimeFormatter
{
    [Inject] private readonly ISparkTimeZoneNames names;

    public async ValueTask<string> FormatAsync(DateTimeOffset value, SparkRecipientLocale recipient, SparkDateTimeFormatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        options ??= new SparkDateTimeFormatOptions();
        var culture = Culture(recipient.Culture);
        var recipientZone = SparkTimeZones.FindOrUtc(recipient.TimeZoneId);

        var primary = Resolve(value, options.Primary, options, recipientZone) ?? new Rendering(SparkTimeZones.ToZone(value, recipientZone), null, false);
        var main = await RenderAsync(primary, options.Style, culture, cancellationToken);

        if (options.Hint is null || Resolve(value, options.Hint, options, recipientZone) is not { } hint)
            return main;

        // Hidden when both give the same wall clock at this instant: compared by offset on the value's date,
        // never by zone name, so Europe/Paris against Europe/Brussels shows one time.
        if (SameWallClock(primary.Local, hint.Local))
            return main;

        return $"{main} ({await RenderAsync(hint, options.Style, culture, cancellationToken)})";
    }

    /// <summary>One rendering: the wall clock to print, and either the zone it is labelled with or <see cref="Stored"/>.</summary>
    private sealed record Rendering(DateTimeOffset Local, string? LabelZone, bool Stored);

    /// <summary>Null for a rendering that cannot be resolved (a <c>zoneFrom</c> without a value): the caller falls back or omits it.</summary>
    private static Rendering? Resolve(DateTimeOffset value, SparkDateTimeZone spec, SparkDateTimeFormatOptions options, TimeZoneInfo recipientZone)
    {
        return spec.Kind switch
        {
            ESparkDateTimeZoneKind.Viewer => new Rendering(SparkTimeZones.ToZone(value, recipientZone), null, false),
            ESparkDateTimeZoneKind.Stored => new Rendering(value, null, true),
            ESparkDateTimeZoneKind.Zone => InZone(spec.Zone),
            _ => InZone(spec.ZoneFrom is { } attribute ? options.ZoneOf?.Invoke(attribute) : null),
        };

        Rendering? InZone(string? id)
            => SparkTimeZones.Find(id) is { } zone ? new Rendering(SparkTimeZones.ToZone(value, zone), id!.Trim(), false) : null;
    }

    private async ValueTask<string> RenderAsync(Rendering rendering, ESparkDateTimeStyle style, CultureInfo culture, CancellationToken cancellationToken)
    {
        var text = rendering.Local.DateTime.ToString(Pattern(style), culture);
        if (rendering.Stored)
            return $"{text} {OffsetLabel(rendering.Local.Offset)}";

        return rendering.LabelZone is { } zone
            ? $"{text} {await names.GetLabelAsync(zone, culture.Name, cancellationToken)}"
            : text;
    }

    private static bool SameWallClock(DateTimeOffset a, DateTimeOffset b)
        => a.DateTime.Ticks / TimeSpan.TicksPerMinute == b.DateTime.Ticks / TimeSpan.TicksPerMinute;

    private static string Pattern(ESparkDateTimeStyle style) => style switch
    {
        ESparkDateTimeStyle.LongDateTime => "f",
        ESparkDateTimeStyle.Date => "d",
        ESparkDateTimeStyle.Time => "t",
        _ => "g",
    };

    /// <summary><c>UTC</c>, <c>UTC+02:00</c>, <c>UTC-03:30</c>. Must match the component's stored label.</summary>
    internal static string OffsetLabel(TimeSpan offset)
        => offset == TimeSpan.Zero ? "UTC" : $"UTC{(offset < TimeSpan.Zero ? '-' : '+')}{offset.Duration():hh\\:mm}";

    private static CultureInfo Culture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return CultureInfo.InvariantCulture;
        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
