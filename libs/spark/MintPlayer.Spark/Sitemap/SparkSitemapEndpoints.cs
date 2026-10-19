using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.PageMeta;

namespace MintPlayer.Spark.Sitemap;

/// <summary>Marks that a sitemap source is registered, so <c>MapSpark()</c> maps the discovery endpoints.</summary>
internal sealed class SparkSitemapMarker;

/// <summary>
/// <c>/sitemap.xml</c> (the index), <c>/sitemaps/{source}/{page}.xml</c> and, opted in, <c>/robots.txt</c> (commerce PRD
/// X22). Mapped by <c>MapSpark()</c> only when a source is registered, at the site root where crawlers look.
/// </summary>
internal static partial class SparkSitemapEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        if (endpoints.ServiceProvider.GetService<SparkSitemapMarker>() is null)
            return;

        endpoints.MapGet("/sitemap.xml", (Delegate)IndexAsync).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapGet("/sitemaps/{source}/{page:int:min(1)}.xml", PageAsync).AllowAnonymous().ExcludeFromDescription();

        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<SparkSitemapOptions>>().Value;
        if (options.RobotsTxt)
            endpoints.MapGet("/robots.txt", Robots).AllowAnonymous().ExcludeFromDescription();
    }

    internal static async Task<IResult> IndexAsync(HttpContext context)
    {
        if (Prepare(context) is not { } prepared)
            return Results.NotFound();
        var (options, origin, sources) = prepared;
        var pageSize = PageSize(options);

        var sitemaps = new List<Uri>();
        foreach (var source in sources)
        {
            var count = await source.CountAsync(new SparkSitemapContext(context, origin), context.RequestAborted);
            var pages = (count + pageSize - 1) / pageSize;
            for (var page = 1; page <= pages; page++)
                sitemaps.Add(new Uri(origin, $"sitemaps/{source.Name}/{page}.xml"));
        }

        return Xml(context, options, SparkSitemapXml.Index(sitemaps));
    }

    internal static async Task<IResult> PageAsync(HttpContext context, string source, int page)
    {
        if (Prepare(context) is not { } prepared)
            return Results.NotFound();
        var (options, origin, sources) = prepared;
        if (sources.FirstOrDefault(s => string.Equals(s.Name, source, StringComparison.Ordinal)) is not { } match)
            return Results.NotFound();

        var pageSize = PageSize(options);
        var entries = await match.GetEntriesAsync(new SparkSitemapContext(context, origin), (page - 1) * pageSize, pageSize, context.RequestAborted);
        if (entries.Count == 0 && page > 1)
            return Results.NotFound();
        return Xml(context, options, SparkSitemapXml.UrlSet(entries.Take(pageSize), origin));
    }

    internal static IResult Robots(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<SparkSitemapOptions>>().Value;
        var body = new StringBuilder(options.RobotsTxtBody.ReplaceLineEndings("\n"));
        if (body.Length > 0 && body[^1] != '\n')
            body.Append('\n');
        if (Origin(context, options) is { } origin)
            body.Append("Sitemap: ").Append(new Uri(origin, "sitemap.xml").AbsoluteUri).Append('\n');
        context.Response.Headers.CacheControl = $"public, max-age={(int)options.CacheDuration.TotalSeconds}";
        return Results.Text(body.ToString(), "text/plain; charset=utf-8");
    }

    private static (SparkSitemapOptions Options, Uri Origin, IReadOnlyList<ISparkSitemapSource> Sources)? Prepare(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<SparkSitemapOptions>>().Value;
        if (!options.Enabled || Origin(context, options) is not { } origin)
            return null;
        var sources = context.RequestServices.GetServices<ISparkSitemapSource>().ToList();
        foreach (var source in sources)
            if (!SourceName().IsMatch(source.Name))
                throw new InvalidOperationException($"Sitemap source {source.GetType().Name} has the name '{source.Name}'; use lower-case letters, digits and dashes.");
        return (options, origin, sources);
    }

    private static int PageSize(SparkSitemapOptions options) => Math.Clamp(options.PageSize, 1, SparkSitemapXml.MaxUrlsPerFile);

    /// <summary>The public origin ending in <c>/</c>: the sitemap's, else page meta's, else <c>Spark:Auth:PublicBaseUrl</c>, else (Development) the request's.</summary>
    private static Uri? Origin(HttpContext context, SparkSitemapOptions options)
    {
        var services = context.RequestServices;
        var configured = options.PublicOrigin ?? services.GetService<IOptions<SparkPageMetaOptions>>()?.Value.PublicOrigin;
        var origin = SparkPublicBaseUrl.Resolve(configured, services.GetService<IConfiguration>(), services.GetService<IHostEnvironment>(), context);
        return origin is not null && Uri.TryCreate(origin + "/", UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
    }

    private static IResult Xml(HttpContext context, SparkSitemapOptions options, byte[] xml)
    {
        context.Response.Headers.CacheControl = $"public, max-age={(int)options.CacheDuration.TotalSeconds}";
        return Results.Bytes(xml, "application/xml; charset=utf-8");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex SourceName();
}
