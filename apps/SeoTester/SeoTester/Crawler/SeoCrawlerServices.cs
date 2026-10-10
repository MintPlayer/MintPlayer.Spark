using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Http;

namespace SeoTester.Crawler;

/// <summary>The crawl pipeline's services (<c>docs/seo_analyzer_PRD.md</c> §4.3, M9).</summary>
public static class SeoCrawlerServices
{
    /// <summary>
    /// The two SSRF-safe clients (X2) and the pipeline's services. Both clients read <c>Spark:SafeHttp</c> first (an
    /// operator can widen or narrow the ports and networks there), then their own limits below.
    /// </summary>
    public static IServiceCollection AddSeoCrawler(this IServiceCollection services, IConfiguration configuration)
    {
        var safeHttp = configuration.GetSection("Spark:SafeHttp");
        // Every request to an audited site: redirects followed and re-checked per hop (traced for the redirect checks),
        // 30 s per exchange, 10 MiB on the wire and 50 MiB decompressed (the defaults), no cookies, no proxy.
        services.AddSafeHttpClient(SeoHttp.Crawler, safeHttp, o =>
        {
            o.MaxRedirects = 5;
            o.Timeout = TimeSpan.FromSeconds(30);
        });
        // Registries (RDAP, IANA) and hstspreload.org: public services, still fetched under the same rules.
        services.AddSafeHttpClient(SeoHttp.Lookups, safeHttp, o =>
        {
            o.MaxRedirects = 3;
            o.Timeout = TimeSpan.FromSeconds(20);
            o.MaxResponseBytes = 2 * 1024 * 1024;
        });

        services.AddOptions<CrawlOptions>().Bind(configuration.GetSection("SeoTester:Crawl"));
        services.AddSingleton<PageFetcher>();
        services.AddSingleton<ITlsProber, TlsProber>();
        services.AddScoped<ExternalLookups>();
        services.AddScoped<SiteProber>();
        services.AddScoped<RunPipeline>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
