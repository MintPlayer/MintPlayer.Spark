using SeoTester.Fetcher.Endpoints;
using SeoTester.Fetching;

namespace SeoTester.Fetcher;

/// <summary>
/// The seo-fetcher service (<c>docs/seo_analyzer_PRD.md</c> D54): SeoTester's in-process fetcher
/// (<see cref="LocalSeoFetcher"/>: the SSRF-safe clients, the TLS probes, DNS) behind a small internal API.
/// <list type="bullet">
/// <item><b>Trust boundary.</b> Every call but <c>GET /health</c> must carry the shared secret
/// (<c>SeoFetcher:Key</c>, compared in constant time); otherwise 401 and nothing else is read. The check is middleware
/// that runs before endpoint execution, so no endpoint's binder ever touches the body of a call without the key.</item>
/// <item><b>Requests are bounded:</b> at most <see cref="SeoFetchLimits.MaxApiRequestBytes"/> read
/// (<see cref="FetcherRequestBody"/>), every field checked (<see cref="SeoFetchLimits.Problem"/>: http(s) URLs only, a
/// known client, its methods, header names and sizes), one call at most <see cref="SeoFetchLimits.OperationTimeout"/>
/// (<see cref="FetcherCallBound"/>).</item>
/// <item><b>SafeHttp stays on</b> (defense in depth): the fetcher refuses private addresses and re-checks every redirect
/// exactly as the app did, so a caller holding the key still cannot point it at the fetcher's own networks.</item>
/// <item>No database, no mail, no logging of headers or the key: the API key of PageSpeed Insights travels in a request
/// header per call and is only passed on.</item>
/// </list>
/// The routes are generator endpoint classes (<c>Endpoints/</c>), mapped by <c>MapFetcherEndpoints</c>. The endpoints
/// package (<c>MintPlayer.AspNetCore.Endpoints</c>) depends on ASP.NET Core only, so the fetcher still carries no Spark
/// runtime and no RavenDB client (<c>FetcherIsolationTests</c>).
/// </summary>
public static class FetcherHost
{
    /// <summary>The shared secret's configuration key (environment: <c>SeoFetcher__Key</c>).</summary>
    public const string KeySetting = "SeoFetcher:Key";

    /// <summary>One call's bound on the fetcher, at most (and by default) <see cref="SeoFetchLimits.OperationTimeout"/>.</summary>
    public const string OperationTimeoutSetting = "SeoFetcher:OperationTimeout";

    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSeoFetching(builder.Configuration);
        return builder;
    }

    /// <summary>Builds the app; refuses without a key of at least <see cref="SeoFetcherProtocol.MinKeyLength"/> characters.</summary>
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        // One call's bound; only ever lowered (tests), never raised past the default the app's deadline is built on.
        var timeout = builder.Configuration.GetValue(OperationTimeoutSetting, SeoFetchLimits.OperationTimeout);
        if (timeout <= TimeSpan.Zero || timeout > SeoFetchLimits.OperationTimeout)
            timeout = SeoFetchLimits.OperationTimeout;
        builder.Services.AddSingleton(new FetcherCallBound(timeout));

        var app = builder.Build();
        var key = app.Configuration[KeySetting];
        if (key is null || key.Length < SeoFetcherProtocol.MinKeyLength)
            throw new InvalidOperationException($"{KeySetting} must hold the shared secret SeoTester calls with, at least {SeoFetcherProtocol.MinKeyLength} characters.");

        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == SeoFetcherProtocol.HealthPath)
            {
                await next();
                return;
            }
            if (!SeoFetcherProtocol.KeyMatches(context.Request.Headers[SeoFetcherProtocol.KeyHeader].ToString(), key))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next();
        });

        app.MapFetcherEndpoints();
        return app;
    }
}
