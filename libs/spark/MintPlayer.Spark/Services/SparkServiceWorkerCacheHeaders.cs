namespace MintPlayer.Spark.Services;

/// <summary>
/// PWA support (#464, D8): the files that decide which version of the SPA a browser runs are
/// served with <c>Cache-Control: no-cache</c>, so a deploy is seen on the next update check rather
/// than when an HTTP cache entry expires.
/// </summary>
/// <remarks>
/// <para>
/// The paths are Angular's service worker (<c>ngsw-worker.js</c>), its manifest (<c>ngsw.json</c>),
/// the kill switch (<c>safety-worker.js</c>, and <c>worker-basic.min.js</c>, the legacy copy of it
/// that <c>@angular/build</c> also emits) and the web app manifest. The browser already revalidates
/// a service worker script at most every 24 h, but <c>ngsw.json</c> is fetched like any other file,
/// and a cached copy would hide a release, or the kill switch, for as long as the cache lives.
/// </para>
/// <para>
/// An <see cref="IStartupFilter"/> (the <see cref="SparkForwardedHeaders"/> pattern) rather than a
/// step inside <c>UseSpark()</c>: the static-file middleware (<c>UseSpaStaticFilesImproved</c>)
/// runs before <c>UseSpark()</c> and ends the request, so a later step would never run. The header
/// is set in <see cref="HttpResponse.OnStarting(Func{Task})"/>, so it is applied whatever later
/// middleware serves the file, and overrides a <c>Cache-Control</c> that middleware wrote.
/// </para>
/// </remarks>
internal static class SparkServiceWorkerCacheHeaders
{
    /// <summary>The request paths that get <c>Cache-Control: no-cache</c>. Exact match, case-insensitive.</summary>
    internal static readonly IReadOnlySet<string> NoCachePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "/ngsw-worker.js",
        "/ngsw.json",
        "/safety-worker.js",
        "/worker-basic.min.js",
        "/manifest.webmanifest",
    };

    public static IServiceCollection AddSparkServiceWorkerCacheHeaders(this IServiceCollection services)
    {
        services.AddTransient<IStartupFilter, StartupFilter>();
        return services;
    }

    /// <summary>The middleware itself, separated so it can be tested without a host.</summary>
    internal static Task Invoke(HttpContext context, Func<Task> next)
    {
        if (context.Request.Path.HasValue && NoCachePaths.Contains(context.Request.Path.Value!))
        {
            var response = context.Response;
            response.OnStarting(() =>
            {
                response.Headers.CacheControl = "no-cache";
                return Task.CompletedTask;
            });
        }

        return next();
    }

    private sealed class StartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) => Invoke(context, () => nextMiddleware(context)));
            next(app);
        };
    }
}
