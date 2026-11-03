using MintPlayer.AspNetCore.SitemapXml;
using MintPlayer.AspNetCore.SitemapXml.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.SitemapXml;

/// <summary>
/// <c>spark.AddSitemap()</c> (<c>docs/spark_ssr_PRD.md</c> D17): a sitemap index at <see cref="SparkSitemapOptions.Path"/>
/// and one sitemap per page of each indexable type's list, mapped by <c>MapSpark()</c> with the other modules' endpoints.
/// </summary>
public static class SparkSitemapExtensions
{
    /// <summary>
    /// Serves the sitemap. Every list runs as an anonymous visitor, whoever asks, so a signed-in owner never sees their
    /// private objects in it, and each request reads one page of one list (or each list's count, for the index).
    /// </summary>
    public static ISparkBuilder AddSitemap(this ISparkBuilder builder, Action<SparkSitemapOptions>? configure = null)
    {
        var options = new SparkSitemapOptions();
        configure?.Invoke(options);
        // MintPlayer.AspNetCore.SitemapXml's options name the stylesheet its endpoint serves. Not AddSitemapXml(): that
        // registers MVC with views for its output formatter, which a Spark app does not use (SparkSitemapXml.Write).
        builder.Services.Configure<SitemapXmlOptions>(o => o.StylesheetUrl = options.StylesheetUrl ?? "");
        builder.Registry.AddEndpoints(endpoints => Map(endpoints, options));
        return builder;
    }

    private static void Map(IEndpointRouteBuilder endpoints, SparkSitemapOptions options)
    {
        var cacheControl = $"public, max-age={(int)options.CacheDuration.TotalSeconds}";
        var pagePrefix = options.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? options.Path[..^4] : options.Path;

        endpoints.MapGet(options.Path, async (HttpContext context, IServiceScopeFactory scopes, IModelLoader models, IQueryLoader queries) =>
        {
            var origin = Origin(context);
            var index = await SparkSitemapXml.IndexAsync(context, scopes, models, queries,
                (type, page) => $"{origin}{pagePrefix}/{Uri.EscapeDataString(type)}/{page}.xml", context.RequestAborted);
            context.Response.Headers.CacheControl = cacheControl;
            return Results.Text(SparkSitemapXml.Write(index, options.StylesheetUrl), "application/xml; charset=utf-8");
        }).AllowAnonymous().ExcludeFromDescription();

        endpoints.MapGet(pagePrefix + "/{type}/{page:int:min(1)}.xml", async (string type, int page, HttpContext context, IServiceScopeFactory scopes, IModelLoader models, IQueryLoader queries) =>
        {
            var urls = await SparkSitemapXml.PageAsync(context, scopes, models, queries, Origin(context), type, page, context.RequestAborted);
            if (urls is null)
                return Results.NotFound();
            context.Response.Headers.CacheControl = cacheControl;
            return Results.Text(SparkSitemapXml.Write(urls, options.StylesheetUrl), "application/xml; charset=utf-8");
        }).AllowAnonymous().ExcludeFromDescription();

        if (options.StylesheetUrl is not null)
            endpoints.MapDefaultSitemapXmlStylesheet().AllowAnonymous().ExcludeFromDescription();
    }

    private static string Origin(HttpContext context)
        => $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
}

/// <summary>Options of <see cref="SparkSitemapExtensions.AddSitemap"/>.</summary>
public sealed class SparkSitemapOptions
{
    /// <summary>
    /// The sitemap index. Default <c>/sitemap.xml</c>; its pages are <c>/sitemap/{type}/{page}.xml</c>. Name it in the
    /// app's robots.txt.
    /// </summary>
    public string Path { get; set; } = "/sitemap.xml";

    /// <summary>
    /// MintPlayer.AspNetCore.SitemapXml's stylesheet, which shows the sitemap as a table in a browser. Default
    /// <c>/sitemap.xsl</c>; null serves none.
    /// </summary>
    public string? StylesheetUrl { get; set; } = "/sitemap.xsl";

    /// <summary>How long browsers and proxies may keep a sitemap response. Default one hour.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(1);
}
