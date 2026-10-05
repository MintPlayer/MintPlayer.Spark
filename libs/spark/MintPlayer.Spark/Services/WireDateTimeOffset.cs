using System.Globalization;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The one reading of a <see cref="DateTimeOffset"/> written as text on the wire: an offset that is
/// present is kept, an offset that is absent means UTC.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the write path (<see cref="EntityMapper"/>) and the query filter path
/// (<c>QueryExecutor.TryConvertFilterValue</c>), because they disagreed. The write path always used
/// <see cref="DateTimeStyles.AssumeUniversal"/>; the filter path handed the string to
/// System.Text.Json, which reads an offset-less value as <b>server-local</b>. The same string then
/// named two instants an hour or two apart, moving with the server's zone and with DST
/// (docs/datetimeoffset_query_sort_filter_PRD.md, F2).
/// </para>
/// <para>
/// <see cref="DateTimeStyles.AssumeUniversal"/>, not <c>RoundtripKind</c> (which reads a missing
/// offset as local) and not <c>AdjustToUniversal</c> (which flattens every offset to +00:00).
/// </para>
/// </remarks>
internal static class WireDateTimeOffset
{
    private const DateTimeStyles Styles = DateTimeStyles.AssumeUniversal;

    /// <exception cref="FormatException"><paramref name="text"/> is not a date.</exception>
    public static DateTimeOffset Parse(string text)
        => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, Styles);

    public static bool TryParse(string? text, out DateTimeOffset value)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, Styles, out value);
}
