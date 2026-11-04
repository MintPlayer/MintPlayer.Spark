using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.SitemapXml;

/// <summary>
/// <c>GET {SparkSitemapOptions.Path}</c> (default <c>/sitemap.xml</c>): the sitemap index, one sitemap per page of each
/// indexable type's list.
/// </summary>
/// <remarks>Anonymous, and out of the API description: crawlers read it, and it is not part of the application's API.</remarks>
internal sealed partial class SitemapIndexEndpoint : IGetEndpoint
{
    /// <summary>The route with the default options; <see cref="IEndpointBase.GetPath"/> maps the configured one.</summary>
    public static string Path => "/sitemap.xml";

    static string? IEndpointBase.GetPath(IServiceProvider services)
        => services.GetRequiredService<SparkSitemapOptions>().Path;

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.AllowAnonymous().ExcludeFromDescription();

    [Inject] private readonly SparkSitemapReader reader;
    [Inject] private readonly SparkSitemapOptions options;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var index = await reader.IndexAsync(httpContext);
        httpContext.Response.Headers.CacheControl = options.CacheControl;
        return Results.Text(SparkSitemapXml.Write(index, options.StylesheetUrl), "application/xml; charset=utf-8");
    }
}

/// <summary>
/// <c>GET {pages}/{type}/{page}.xml</c> (default <c>/sitemap/{type}/{page}.xml</c>): one page of one indexable type's
/// list. 404 for a type that is not indexable or a list an anonymous visitor cannot read.
/// </summary>
internal sealed partial class SitemapPageEndpoint : IGetEndpoint
{
    /// <summary>The route with the default options; <see cref="IEndpointBase.GetPath"/> maps the configured one.</summary>
    public static string Path => "/sitemap/{type}/{page:int:min(1)}.xml";

    static string? IEndpointBase.GetPath(IServiceProvider services)
        => services.GetRequiredService<SparkSitemapOptions>().PagePrefix + "/{type}/{page:int:min(1)}.xml";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.AllowAnonymous().ExcludeFromDescription();

    [RouteParam] public string Type { get; set; } = "";
    [RouteParam] public int Page { get; set; }

    [Inject] private readonly SparkSitemapReader reader;
    [Inject] private readonly SparkSitemapOptions options;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (await reader.PageAsync(httpContext, Type, Page) is not { } urls)
            return Results.NotFound();
        httpContext.Response.Headers.CacheControl = options.CacheControl;
        return Results.Text(SparkSitemapXml.Write(urls, options.StylesheetUrl), "application/xml; charset=utf-8");
    }
}
