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

        // The headless browser's requests (M10, §6): each one fulfilled through this client, one hop at a time (Chromium
        // follows the redirect and asks again, so every hop is checked), 30 s, the default byte caps.
        services.AddSafeHttpClient(SeoHttp.Browser, safeHttp, o =>
        {
            o.FollowRedirects = false;
            o.Timeout = TimeSpan.FromSeconds(30);
        });
        // PageSpeed Insights runs Lighthouse while we wait (8–30 s measured, S5) and answers 0.3–0.4 MB with the field mask.
        // 100 s each: five pages stay under seo-psi's 10-minute handler timeout.
        services.AddSafeHttpClient(SeoHttp.PageSpeed, safeHttp, o =>
        {
            o.MaxRedirects = 0;
            o.Timeout = TimeSpan.FromSeconds(100);
            o.MaxResponseBytes = 8 * 1024 * 1024;
            o.MaxDecompressedBytes = 32 * 1024 * 1024;
        });

        services.AddOptions<Rendering.BrowserOptions>().Bind(configuration.GetSection("SeoTester:Browser"));
        services.AddSingleton<Rendering.ChromiumBrowser>();
        services.AddSingleton<Rendering.RenderGate>();
        services.AddSingleton<Rendering.PageRenderer>();
        services.AddOptions<PageSpeed.PageSpeedOptions>().Bind(configuration.GetSection("SeoTester:PageSpeed"));
        services.AddScoped<PageSpeed.PageSpeedClient>();

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
