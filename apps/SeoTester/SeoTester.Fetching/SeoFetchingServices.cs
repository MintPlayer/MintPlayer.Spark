using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Http;
using SeoTester.Crawler;
using SeoTester.Dns;

namespace SeoTester.Fetching;

/// <summary>Registers one of the two <see cref="ISeoFetcher"/>s (<c>docs/seo_analyzer_PRD.md</c> D54).</summary>
public static class SeoFetchingServices
{
    /// <summary>The named client of <see cref="RemoteSeoFetcher"/>.</summary>
    public const string FetcherClient = "SeoTester.Fetcher";

    /// <summary>
    /// The mode <c>SeoTester:Fetcher:Mode</c> names, else Remote in Production and InProcess elsewhere. Production refuses
    /// InProcess: the point of the fetcher is that the process holding the database never connects to a target.
    /// </summary>
    public static SeoFetcherMode ResolveMode(IConfiguration configuration, IHostEnvironment environment)
    {
        var mode = configuration.GetSection(SeoFetcherClientOptions.SectionName).GetValue<SeoFetcherMode?>(nameof(SeoFetcherClientOptions.Mode))
            ?? (environment.IsProduction() ? SeoFetcherMode.Remote : SeoFetcherMode.InProcess);
        if (mode == SeoFetcherMode.InProcess && environment.IsProduction())
            throw new InvalidOperationException(
                "SeoTester:Fetcher:Mode is InProcess in Production. Target traffic runs in the seo-fetcher container there (docs/seo_analyzer_PRD.md D54): set SeoTester:Fetcher:Url and SeoTester:Fetcher:Key.");
        return mode;
    }

    /// <summary>
    /// The in-process fetcher: the SSRF-safe clients (X2) under <c>Spark:SafeHttp</c> plus their own limits, the TLS
    /// prober and DNS. The fetcher container calls this; so does the app outside Production. Registrations a test made
    /// earlier for <see cref="ISeoDns"/>, <see cref="ITlsProber"/> or <see cref="ISeoFetcher"/> are kept.
    /// </summary>
    public static IServiceCollection AddSeoFetching(this IServiceCollection services, IConfiguration configuration)
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
        // Ownership proofs (F3): a proof must come from the site itself, an open redirect elsewhere proves nothing.
        services.AddSafeHttpClient(SeoHttp.Verification, safeHttp, o =>
        {
            o.RedirectsWithinOriginOnly = true;
            o.MaxRedirects = 3;
            o.Timeout = TimeSpan.FromSeconds(15);
        });

        services.TryAddSingleton<PageFetcher>();
        services.TryAddSingleton<ITlsProber, TlsProber>();
        services.TryAddSingleton<ISeoDns, DnsClientSeoDns>();
        services.TryAddSingleton<ISeoFetcher, LocalSeoFetcher>();
        return services;
    }

    /// <summary>
    /// The fetcher container as <see cref="ISeoFetcher"/>: <c>SeoTester:Fetcher:Url</c> and <c>:Key</c> are required.
    /// A plain client (the fetcher is on a private network, which the SSRF-safe client would rightly refuse), no proxy, no
    /// cookies, no redirects.
    /// </summary>
    public static IServiceCollection AddSeoFetcherClient(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SeoFetcherClientOptions.SectionName);
        services.AddOptions<SeoFetcherClientOptions>().Bind(section);
        var url = section[nameof(SeoFetcherClientOptions.Url)];
        var key = section[nameof(SeoFetcherClientOptions.Key)];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseAddress) || baseAddress.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("SeoTester:Fetcher:Url must be the fetcher's absolute http(s) address (e.g. http://seo-fetcher:8080/) when the fetcher is Remote.");
        if (key is null || key.Length < SeoFetcherProtocol.MinKeyLength)
            throw new InvalidOperationException($"SeoTester:Fetcher:Key must hold the fetcher's shared secret, at least {SeoFetcherProtocol.MinKeyLength} characters, when the fetcher is Remote.");
        if (!baseAddress.AbsoluteUri.EndsWith('/'))
            baseAddress = new Uri(baseAddress.AbsoluteUri + "/");

        services.RemoveAll<ISeoFetcher>();
        services.AddHttpClient<ISeoFetcher, RemoteSeoFetcher>(FetcherClient, client =>
            {
                client.BaseAddress = baseAddress;
                // Each call has its own deadline (RemoteSeoFetcher); this only stops a call that somehow has none.
                client.Timeout = TimeSpan.FromMinutes(5);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseProxy = false,
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });
        return services;
    }
}
