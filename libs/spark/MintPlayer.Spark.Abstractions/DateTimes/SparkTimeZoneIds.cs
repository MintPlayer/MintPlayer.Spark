using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.Abstractions.DateTimes;

/// <summary>
/// The shape check every IANA zone id passes before anything looks it up: the <c>X-Spark-Timezone</c>
/// header, the <c>spark-timezone</c> cookie and the account's stored zone (X14).
/// </summary>
public static partial class SparkTimeZoneIds
{
    /// <summary>The longest id accepted (the longest id a browser reports is 30, S-TZ4).</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// An IANA-shaped zone id: a letter, then up to three <c>/</c>-separated segments of letters,
    /// digits, <c>_</c>, <c>+</c> and <c>-</c>. No dots, spaces, backslashes or empty segments, so
    /// nothing path-like reaches a zone lookup (S-TZ1: on Linux <c>TimeZoneInfo.FindSystemTimeZoneById</c>
    /// reads a file under <c>/usr/share/zoneinfo</c>, and resolves <c>Europe//Brussels</c>).
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_+\-]*(/[A-Za-z0-9_+\-]+){0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    /// <summary>
    /// Whether <paramref name="id"/> is IANA-shaped and at most <see cref="MaxLength"/> characters. A shape,
    /// not a lookup: a well-formed id this server's tzdata does not know (a recent rename) still passes, and
    /// resolves to UTC where it is used.
    /// </summary>
    public static bool IsWellFormed([NotNullWhen(true)] string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= MaxLength && Shape().IsMatch(id);
}
