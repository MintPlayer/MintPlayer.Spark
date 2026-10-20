using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.PageMeta;

namespace MintPlayer.Spark.Sitemap;

/// <summary>
/// A library's or an application's public pages for <c>/sitemap.xml</c> (commerce PRD X22, GB9; the
/// <a href="https://www.sitemaps.org/protocol.html">sitemap protocol</a>). Registered with
/// <c>spark.AddSitemapSource&lt;T&gt;()</c>; resolved per request, so a source can be scoped and query its session.
/// </summary>
/// <remarks>
/// Paged: <c>/sitemap.xml</c> is a sitemap index listing <c>/sitemaps/{Name}/{page}.xml</c>, each holding at most
/// <see cref="SparkSitemapOptions.PageSize"/> URLs (the protocol caps a file at 50,000 URLs and 50 MB). Return public
/// pages only: the sitemap is the same for everyone.
/// </remarks>
public interface ISparkSitemapSource
{
    /// <summary>The source's name in its sitemap URLs: lower-case letters, digits and dashes (<c>events</c>).</summary>
    string Name { get; }

    /// <summary>How many entries the source has now; decides the number of pages in the index.</summary>
    Task<int> CountAsync(SparkSitemapContext context, CancellationToken cancellationToken);

    /// <summary>
    /// The entries <paramref name="skip"/> .. <paramref name="skip"/> + <paramref name="take"/>, in a stable order (by id),
    /// so a page holds the same URLs between the index request and the page request.
    /// </summary>
    Task<IReadOnlyList<SparkSitemapEntry>> GetEntriesAsync(SparkSitemapContext context, int skip, int take, CancellationToken cancellationToken);
}

/// <summary>One URL of a sitemap.</summary>
/// <param name="Location">A path (<c>/events/summer-fest</c>), resolved against the public origin, or an absolute URL on
/// that origin. A URL on another origin is left out (the protocol: a sitemap lists URLs of its own host).</param>
/// <param name="LastModified">The page's last change, written as a W3C datetime in UTC.</param>
/// <param name="Alternates">
/// The page's language versions (<c>xhtml:link rel="alternate" hreflang</c>), <b>including itself</b>, and optionally
/// <c>x-default</c>. Every version must list the same set (Google's hreflang rules); a source that emits each language as
/// its own entry gives each the same alternates.
/// </param>
/// <param name="ChangeFrequency">A hint; most crawlers ignore it.</param>
/// <param name="Priority">0.0–1.0, a hint; most crawlers ignore it.</param>
public sealed record SparkSitemapEntry(
    string Location,
    DateTimeOffset? LastModified = null,
    IReadOnlyList<SparkHreflang>? Alternates = null,
    SparkSitemapChangeFrequency? ChangeFrequency = null,
    decimal? Priority = null);

/// <summary>The protocol's <c>changefreq</c> values.</summary>
public enum SparkSitemapChangeFrequency { Always, Hourly, Daily, Weekly, Monthly, Yearly, Never }

/// <summary>What a <see cref="ISparkSitemapSource"/> is asked with.</summary>
public sealed class SparkSitemapContext
{
    internal SparkSitemapContext(HttpContext httpContext, Uri origin)
    {
        HttpContext = httpContext;
        Origin = origin;
    }

    /// <summary>The crawler's request (anonymous: return public pages only).</summary>
    public HttpContext HttpContext { get; }

    /// <summary>The public origin, ending in <c>/</c>.</summary>
    public Uri Origin { get; }

    public IServiceProvider Services => HttpContext.RequestServices;
}

/// <summary><c>Spark:Sitemap</c>. Code given to <c>AddSitemap</c> runs after the section.</summary>
public sealed class SparkSitemapOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Sitemap";

    /// <summary>The global switch. Off: <c>/sitemap.xml</c> answers 404.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>URLs per sitemap file; at most 50,000 (the protocol's limit).</summary>
    public int PageSize { get; set; } = 10_000;

    /// <summary>
    /// The origin of every URL (<c>https://shop.example</c>). Null means <c>Spark:PageMeta:PublicOrigin</c>, then
    /// <c>Spark:Auth:PublicBaseUrl</c>, else (Development only) the request's origin; else the sitemap answers 404.
    /// </summary>
    public string? PublicOrigin { get; set; }

    /// <summary><c>Cache-Control: max-age</c> of the sitemap files.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Serve <c>/robots.txt</c> with <see cref="RobotsTxtBody"/> and a <c>Sitemap:</c> line pointing to the index.
    /// Read when the endpoints are mapped. ⚠️ Remove a static <c>robots.txt</c> from the SPA's assets when turning this
    /// on: the static-file middleware may answer first.
    /// </summary>
    public bool RobotsTxt { get; set; }

    /// <summary>The rules above the <c>Sitemap:</c> line.</summary>
    public string RobotsTxtBody { get; set; } = "User-agent: *\nAllow: /\n";
}
