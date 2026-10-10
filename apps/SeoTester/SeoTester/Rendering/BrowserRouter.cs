using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MintPlayer.Spark.Http;
using SeoTester.Crawler;
using SeoTester.Crawling;
using SeoTester.Fetching;

namespace SeoTester.Rendering;

/// <summary>
/// The network of one run's renders (<c>docs/seo_analyzer_PRD.md</c> §6, M10): every request any of its browser
/// contexts makes is fetched here, through the SSRF-safe client <see cref="SeoHttp.Browser"/> (redirects handed back
/// to Chromium, which asks again, so each hop is checked), politely (the audited host's connections per host; its
/// documents also wait for the tier's interval), within robots.txt for the audited host, with analytics hits answered
/// locally and never sent on, and static resources fetched once per run. The pacing, robots.txt, the sink and the
/// cache stay here, in the app; the request itself is made by <see cref="ISeoFetcher"/>: the fetcher container in
/// Production (D54), so Chromium's traffic leaves from the one process that has no database.
/// </summary>
public sealed class RenderNetwork(ISeoFetcher fetcher, Uri origin, RobotsTxt robots, HostPacer pacer, ILogger logger) : IDisposable
{
    /// <summary>Largest request body passed on (a form post or an API call of the page's script).</summary>
    public const int MaxRequestBodyBytes = SeoFetchLimits.MaxRequestBodyBytes;

    /// <summary>Largest response kept in the per-run cache.</summary>
    private const int MaxCachedBytes = 2 * 1024 * 1024;

    /// <summary>Response headers not handed to Chromium: the body is already decompressed and complete.</summary>
    private static readonly HashSet<string> DroppedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "content-encoding", "content-length", "transfer-encoding", "connection", "keep-alive", "trailer",
    };

    private readonly ConcurrentDictionary<string, RoutedResponse> cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> otherHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> tools = new(StringComparer.Ordinal);
    private int hits;

    public Uri Origin => origin;

    /// <summary>Analytics tools whose scripts loaded, across the run's renders.</summary>
    public IReadOnlyList<string> Tools => tools.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>Analytics hits answered locally so far.</summary>
    public int Hits => Volatile.Read(ref hits);

    /// <summary>A consent manager's script was requested.</summary>
    public bool ConsentManager { get; private set; }

    /// <summary>Requests made to the network on behalf of the browser (cache hits and sinks excluded).</summary>
    public int Fetches => Volatile.Read(ref fetches);
    private int fetches;

    /// <summary>The URL is the audited site's (its host, or the www/apex twin).</summary>
    public bool IsFirstParty(Uri url)
    {
        var host = url.Host;
        var site = origin.Host;
        static string Bare(string h) => h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h[4..] : h;
        return Bare(host).Equals(Bare(site), StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + Bare(site), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Answers one browser request; never throws for what the target does.</summary>
    public async Task<RoutedResponse> FetchAsync(Uri url, string method, IReadOnlyList<KeyValuePair<string, string>> headers, byte[]? body, bool document, CancellationToken cancellationToken)
    {
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            return RoutedResponse.Blocked("scheme");
        if (AnalyticsVendors.ToolForHit(url) is { } hitTool)
        {
            Interlocked.Increment(ref hits);
            tools.TryAdd(hitTool, 0);
            return RoutedResponse.Sink();
        }
        if (AnalyticsVendors.IsConsentManager(url))
            ConsentManager = true;
        var sameHost = url.Host.Equals(origin.Host, StringComparison.OrdinalIgnoreCase);
        if (sameHost && !robots.IsAllowed(url))
            return RoutedResponse.Blocked("robots.txt");
        if (body is { Length: > MaxRequestBodyBytes })
            return RoutedResponse.Blocked("request body too large");

        var cacheable = method == "GET" && !document;
        if (cacheable && cache.TryGetValue(url.AbsoluteUri, out var cached))
            return cached with { FromCache = true };

        IDisposable slot;
        if (sameHost)
            slot = await pacer.WaitAsync(paced: document, cancellationToken);
        else
        {
            var semaphore = otherHosts.GetOrAdd(url.Host, _ => new SemaphoreSlim(4));
            await semaphore.WaitAsync(cancellationToken);
            slot = new Releaser(semaphore);
        }

        using (slot)
        {
            Interlocked.Increment(ref fetches);
            FetchResult answer;
            try
            {
                // The request is passed on as the browser made it (hop-by-hop headers dropped, the crawler's identity kept
                // in the user agent: PageFetcher.SendAsync), by the fetcher (D54) or in this process.
                answer = await fetcher.FetchAsync(new SeoFetchRequest(url, SeoHttp.MaxBodyBytes[SeoHttp.Browser])
                {
                    Method = method,
                    Client = SeoHttp.Browser,
                    Headers = headers,
                    Body = body,
                }, cancellationToken);
            }
            catch (SeoFetcherUnavailableException ex)
            {
                logger.LogWarning("Browser request to {Url} failed: {Reason}", url, ex.Message);
                return RoutedResponse.Failed("fetcher unavailable");
            }
            if (answer.StatusCode == 0)
            {
                if (answer.Refused)
                {
                    logger.LogDebug("Browser request to {Url} refused: {Reason}", url, answer.Error);
                    return RoutedResponse.Blocked(answer.Error!["refused: ".Length..]);
                }
                return RoutedResponse.Failed(answer.Error ?? "no answer");
            }
            // The whole body or nothing: Chromium must not render a cut file as if it were complete.
            if (answer.Truncated)
                return RoutedResponse.Blocked(nameof(SafeHttpBlockReason.ResponseTooLarge));

            var bytes = answer.Body ?? [];
            var responseHeaders = new List<KeyValuePair<string, string>>();
            foreach (var (name, value) in answer.Headers)
                if (!DroppedResponseHeaders.Contains(name))
                    responseHeaders.Add(new(name, value));
            foreach (var cookie in answer.SetCookies)
                responseHeaders.Add(new("set-cookie", cookie));
            var result = new RoutedResponse(answer.StatusCode, responseHeaders, bytes, "fulfilled", null);
            if (answer.ContentType is { } type && (type.Contains("javascript") || type.Contains("ecmascript"))
                && AnalyticsVendors.ToolForScript(url) is { } scriptTool)
                tools.TryAdd(scriptTool, 0);
            if (cacheable && result.Status == 200 && bytes.Length <= MaxCachedBytes
                && answer.Header("cache-control")?.Contains("no-store", StringComparison.OrdinalIgnoreCase) != true)
                cache.TryAdd(url.AbsoluteUri, result);
            return result;
        }
    }

    public void Dispose()
    {
        foreach (var semaphore in otherHosts.Values)
            semaphore.Dispose();
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}

/// <summary>How one browser request was answered.</summary>
/// <param name="Outcome"><c>fulfilled</c>, <c>sink</c> (an analytics hit answered locally), <c>blocked</c> or <c>failed</c>.</param>
public sealed record RoutedResponse(int Status, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[] Body, string Outcome, string? Reason)
{
    public bool FromCache { get; init; }

    public static RoutedResponse Blocked(string reason) => new(0, [], [], "blocked", reason);
    public static RoutedResponse Failed(string reason) => new(0, [], [], "failed", reason);
    public static RoutedResponse Sink() => new(204, [], [], "sink", null);

    public string? Header(string name) => Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>
/// Routes one browser context (§6): every HTTP(S) request through <see cref="RenderNetwork"/>, every WebSocket closed
/// before it connects, and SeoTester's init script in every frame. Keeps the context's network log and the main
/// document as served (the raw HTML of the raw-vs-rendered comparison).
/// </summary>
public sealed class BrowserRouter(RenderNetwork network, CancellationToken cancellationToken)
{
    private static readonly Lazy<string> InitScript = new(() => Resource("SeoTester.Rendering.init.js"));
    private readonly ConcurrentQueue<NetworkEntry> log = new();
    private int documents;

    public IReadOnlyList<NetworkEntry> Log => log.ToList();

    /// <summary>The first main-frame document the context loaded, as served.</summary>
    public RoutedResponse? MainDocument { get; private set; }

    /// <summary>Main-frame documents requested so far (a client-side route change requests none).</summary>
    public int Documents => Volatile.Read(ref documents);

    public async Task AttachAsync(IBrowserContext context)
    {
        await context.AddInitScriptAsync(InitScript.Value);
        await context.RouteWebSocketAsync(new Regex(".*"), ws => { _ = ws.CloseAsync(new() { Code = 1008, Reason = "blocked" }); });
        await context.RouteAsync("**/*", HandleAsync);
    }

    private async Task HandleAsync(IRoute route)
    {
        var request = route.Request;
        try
        {
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url))
            {
                await route.AbortAsync("blockedbyclient");
                return;
            }
            var mainFrameDocument = request.IsNavigationRequest && IsMainFrame(request);
            if (mainFrameDocument)
                Interlocked.Increment(ref documents);
            var headers = (await request.HeadersArrayAsync()).Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToList();
            var answer = await network.FetchAsync(url, request.Method, headers, request.PostDataBuffer, request.IsNavigationRequest, cancellationToken);
            log.Enqueue(new NetworkEntry(Cut(url.AbsoluteUri, 300), request.ResourceType, answer.Status, answer.Body.LongLength, !network.IsFirstParty(url), answer.Outcome));
            if (answer.Outcome is "blocked" or "failed")
            {
                await route.AbortAsync(answer.Outcome == "blocked" ? "blockedbyclient" : "failed");
                return;
            }
            if (mainFrameDocument && MainDocument is null && answer.Status is >= 200 and < 300)
                MainDocument = answer;
            await route.FulfillAsync(new RouteFulfillOptions { Status = answer.Status, Headers = answer.Headers, BodyBytes = answer.Body });
        }
        catch (PlaywrightException)
        {
            // The page or context closed while the answer was on its way.
        }
        catch (OperationCanceledException)
        {
            await TryAbortAsync(route);
        }
    }

    private static bool IsMainFrame(IRequest request)
    {
        try
        {
            return request.Frame.ParentFrame is null;
        }
        catch (PlaywrightException)
        {
            return false; // a service worker's request has no frame
        }
    }

    private static async Task TryAbortAsync(IRoute route)
    {
        try
        {
            await route.AbortAsync("aborted");
        }
        catch (PlaywrightException)
        {
        }
    }

    private static string Cut(string value, int max) => value.Length > max ? value[..max] : value;

    internal static string Resource(string name)
    {
        using var stream = typeof(BrowserRouter).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
