using Microsoft.Extensions.DependencyInjection.Extensions;
using SeoTester.Fetching;

namespace SeoTester.Crawler;

/// <summary>The crawl pipeline's services (<c>docs/seo_analyzer_PRD.md</c> §4.3, M9).</summary>
public static class SeoCrawlerServices
{
    /// <summary>
    /// The pipeline's services and the <see cref="ISeoFetcher"/> it sends target traffic through (D54):
    /// <list type="bullet">
    /// <item><see cref="SeoFetcherMode.InProcess"/>: the SSRF-safe clients (X2), the TLS prober and DNS in this process
    /// (<see cref="SeoFetchingServices.AddSeoFetching"/>; <c>Spark:SafeHttp</c> first, then each client's own limits);</item>
    /// <item><see cref="SeoFetcherMode.Remote"/>: the seo-fetcher container (<c>SeoTester:Fetcher:Url</c>, <c>:Key</c>); this
    /// process then registers no client that can reach a target at all.</item>
    /// </list>
    /// The politeness (HostPacer), robots.txt and the caches stay here either way.
    /// </summary>
    public static IServiceCollection AddSeoCrawler(this IServiceCollection services, IConfiguration configuration, SeoFetcherMode mode = SeoFetcherMode.InProcess)
    {
        if (mode == SeoFetcherMode.Remote)
            services.AddSeoFetcherClient(configuration);
        else
            services.AddSeoFetching(configuration);

        services.AddOptions<Rendering.BrowserOptions>().Bind(configuration.GetSection("SeoTester:Browser"));
        services.AddSingleton<Rendering.ChromiumBrowser>();
        services.AddSingleton<Rendering.RenderGate>();
        services.AddSingleton<Rendering.PageRenderer>();
        services.AddOptions<PageSpeed.PageSpeedOptions>().Bind(configuration.GetSection("SeoTester:PageSpeed"));
        services.AddScoped<PageSpeed.PageSpeedClient>();

        services.AddOptions<CrawlOptions>().Bind(configuration.GetSection("SeoTester:Crawl"));
        services.AddScoped<ExternalLookups>();
        services.AddScoped<SiteProber>();
        services.AddScoped<RunPipeline>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
