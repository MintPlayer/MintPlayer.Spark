namespace MintPlayer.Spark.Configuration;

/// <summary>
/// How <see cref="Services.IRequestTimeZoneResolver"/> learns the viewer's timezone, bound from
/// <c>Spark:TimeZone</c> (#460, item 7).
/// </summary>
/// <remarks>
/// <para>
/// Precedence: a valid <c>X-Spark-Timezone</c> header, then a valid cookie named
/// <see cref="CookieName"/>, then UTC. An invalid header falls through to the cookie. The header is
/// what the browser's HTTP client sends; the cookie exists for requests the browser makes without
/// running any script first — a server-side render of the first page, a link opened from a mail —
/// and is written by ng-spark's <c>withSparkTimezone()</c> in the browser, never by the server.
/// </para>
/// <para>
/// Both values are validated before they reach <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>:
/// at most 64 characters, an IANA-shaped id (<c>Region/City</c>, up to three segments). A Windows id
/// such as <c>Romance Standard Time</c> is refused; <c>MintPlayer.Spark.Client</c> converts one to
/// its IANA id before sending it.
/// </para>
/// </remarks>
public sealed class SparkTimeZoneOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "Spark:TimeZone";

    /// <summary>
    /// The default cookie name. Must stay in lockstep with <c>SPARK_TIMEZONE_COOKIE</c> in ng-spark's
    /// <c>spark-timezone.interceptor.ts</c>.
    /// </summary>
    public const string DefaultCookieName = "spark-timezone";

    /// <summary>
    /// The cookie read when the header is absent or invalid. <c>null</c> or empty disables the cookie
    /// (an environment variable set to an empty string does that); the header still works.
    /// </summary>
    public string? CookieName { get; set; } = DefaultCookieName;
}
