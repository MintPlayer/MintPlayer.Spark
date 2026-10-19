using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.PageMeta;

/// <summary>
/// The tags a public SPA route gets in its server-rendered <c>index.html</c> (commerce PRD X6, D27): the title, the
/// description, and the OpenGraph and Twitter card a link preview is built from. Every value is HTML-encoded by the
/// framework, so seller-written text can be returned as it is.
/// </summary>
/// <param name="Title">The page title: <c>&lt;title&gt;</c>, <c>og:title</c>.</param>
/// <param name="Description">The meta description and <c>og:description</c>.</param>
/// <param name="Image">The preview image. A relative URL is resolved against the public origin; without one it is left out.</param>
/// <param name="Type">The <c>og:type</c>.</param>
/// <param name="Canonical">The canonical URL; <see langword="null"/> means the public origin plus the request path. Also <c>og:url</c>.</param>
/// <param name="TwitterCard">The <c>twitter:card</c>.</param>
public sealed record SparkPageMeta(
    string Title,
    string Description,
    Uri? Image = null,
    string Type = "website",
    Uri? Canonical = null,
    string TwitterCard = "summary_large_image");

/// <summary>What a <see cref="ISparkPageMetaProvider"/> is asked about: the request and the route values its pattern matched.</summary>
public sealed class SparkPageMetaContext
{
    internal SparkPageMetaContext(HttpContext httpContext, string routePattern, RouteValueDictionary routeValues)
    {
        HttpContext = httpContext;
        RoutePattern = routePattern;
        RouteValues = routeValues;
    }

    /// <summary>
    /// The navigation request. The middleware wraps the whole pipeline, so it runs before authentication: the caller is
    /// anonymous here even when signed in. Return public fields only; the page is the same for everyone.
    /// </summary>
    public HttpContext HttpContext { get; }

    /// <summary>The pattern of <see cref="ISparkPageMetaProvider.RoutePatterns"/> that matched.</summary>
    public string RoutePattern { get; }

    /// <summary>The values the pattern captured, URL-decoded (<c>/events/{slug}</c> → <c>slug</c>).</summary>
    public RouteValueDictionary RouteValues { get; }

    /// <summary>The request's services.</summary>
    public IServiceProvider Services => HttpContext.RequestServices;
}

/// <summary>
/// A library's or an application's public SPA routes, and the meta tags each one gets in <c>index.html</c>.
/// Registered with <c>spark.AddPageMetaProvider&lt;T&gt;()</c>; resolved per request, so a provider can be scoped.
/// </summary>
public interface ISparkPageMetaProvider
{
    /// <summary>
    /// Route templates of the SPA's public pages, e.g. <c>/events/{slug}</c> or <c>/shop/products/{id}</c>. Read once per
    /// provider type and compiled; the first matching pattern of the first provider is asked.
    /// </summary>
    IEnumerable<string> RoutePatterns { get; }

    /// <summary>
    /// The tags for the matched page, or <see langword="null"/> to serve <c>index.html</c> untouched (an unknown or
    /// non-public object: no soft 404, the SPA shows its own not-found page).
    /// </summary>
    Task<SparkPageMeta?> GetAsync(SparkPageMetaContext context, CancellationToken cancellationToken);
}

/// <summary><c>Spark:PageMeta</c>. Code given to <c>AddPageMeta</c> runs after the section.</summary>
public sealed class SparkPageMetaOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:PageMeta";

    /// <summary>The global switch. Off: every request passes through untouched.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The origin of <c>og:url</c>, the canonical link and relative images, e.g. <c>https://shop.example</c>. Null means
    /// <c>Spark:Auth:PublicBaseUrl</c>, else (Development only) the request's origin, else those tags are left out.
    /// Never the request's <c>Host</c> outside Development: a crawler's cached card would carry whatever host the
    /// caller sent.
    /// </summary>
    public string? PublicOrigin { get; set; }
}
