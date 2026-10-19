namespace MintPlayer.Spark.Abstractions.DateTimes;

/// <summary>
/// Formats an instant for server-rendered output (mails, PDFs, scanner screens) with the same semantics
/// as ng-spark's <c>&lt;spark-date-time&gt;</c> (commerce PRD X16, D34), where "viewer" means the recipient
/// (<see cref="ISparkRecipientLocale"/>, X14). The only sanctioned way to turn a <see cref="DateTimeOffset"/>
/// into local text outside the core framework (K10).
/// </summary>
/// <example>
/// <code>
/// var recipient = await locales.ForUserAsync(order.UserId, ct);
/// var starts = await formatter.FormatAsync(evt.StartsAt, recipient, new SparkDateTimeFormatOptions
/// {
///     Hint = SparkDateTimeZone.InZone(venue.TimeZoneId),   // "21:00 (20:00 London)"
///     Style = ESparkDateTimeStyle.LongDateTime,
/// }, ct);
/// </code>
/// </example>
public interface ISparkDateTimeFormatter
{
    /// <summary>
    /// <paramref name="value"/> in the recipient's culture: its main rendering, then the hint in parentheses
    /// when the hint's wall clock differs from the main one at that instant.
    /// </summary>
    ValueTask<string> FormatAsync(DateTimeOffset value, SparkRecipientLocale recipient, SparkDateTimeFormatOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>How much of the date and time is written.</summary>
public enum ESparkDateTimeStyle
{
    /// <summary>Short date and short time (<c>"g"</c>): <c>04/07/2026 09:00</c>.</summary>
    DateTime,
    /// <summary>Long date and short time (<c>"f"</c>): <c>zaterdag 4 juli 2026 09:00</c>.</summary>
    LongDateTime,
    /// <summary>Short date only (<c>"d"</c>).</summary>
    Date,
    /// <summary>Short time only (<c>"t"</c>).</summary>
    Time,
}

/// <summary>The zones and style of one <see cref="ISparkDateTimeFormatter.FormatAsync"/> call.</summary>
public sealed record SparkDateTimeFormatOptions
{
    /// <summary>The main rendering; the recipient's zone by default.</summary>
    public SparkDateTimeZone Primary { get; init; } = SparkDateTimeZone.Viewer;

    /// <summary>An optional second rendering, hidden when its wall clock equals the main one.</summary>
    public SparkDateTimeZone? Hint { get; init; }

    public ESparkDateTimeStyle Style { get; init; } = ESparkDateTimeStyle.DateTime;

    /// <summary>
    /// Resolves a <see cref="ESparkDateTimeZoneKind.ZoneFrom"/> attribute name to the IANA id it holds on the
    /// object being formatted. Without it, or when it answers <see langword="null"/>, that rendering is the
    /// recipient's zone (main) or left out (hint).
    /// </summary>
    public Func<string, string?>? ZoneOf { get; init; }

    /// <summary>The options a model attribute's <see cref="SparkDateTimeDisplay"/> declares.</summary>
    public static SparkDateTimeFormatOptions From(SparkDateTimeDisplay? display, Func<string, string?>? zoneOf = null, ESparkDateTimeStyle style = ESparkDateTimeStyle.DateTime) => new()
    {
        Primary = display?.Primary ?? SparkDateTimeZone.Viewer,
        Hint = display?.Hint,
        Style = style,
        ZoneOf = zoneOf,
    };
}
