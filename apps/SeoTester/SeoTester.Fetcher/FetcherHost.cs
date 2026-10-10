using System.Text.Json;
using SeoTester.Crawler;
using SeoTester.Dns;
using SeoTester.Fetching;

namespace SeoTester.Fetcher;

/// <summary>
/// The seo-fetcher service (<c>docs/seo_analyzer_PRD.md</c> D54): SeoTester's in-process fetcher
/// (<see cref="LocalSeoFetcher"/>: the SSRF-safe clients, the TLS probes, DNS) behind a small internal API.
/// <list type="bullet">
/// <item><b>Trust boundary.</b> Every call but <c>GET /health</c> must carry the shared secret
/// (<c>SeoFetcher:Key</c>, compared in constant time); otherwise 401 and nothing else is read.</item>
/// <item><b>Requests are bounded:</b> at most <see cref="SeoFetchLimits.MaxApiRequestBytes"/> read, every field checked
/// (<see cref="SeoFetchLimits.Problem"/>: http(s) URLs only, a known client, its methods, header names and sizes), one
/// call at most <see cref="SeoFetchLimits.OperationTimeout"/>.</item>
/// <item><b>SafeHttp stays on</b> (defense in depth): the fetcher refuses private addresses and re-checks every redirect
/// exactly as the app did, so a caller holding the key still cannot point it at the fetcher's own networks.</item>
/// <item>No database, no mail, no logging of headers or the key: the API key of PageSpeed Insights travels in a request
/// header per call and is only passed on.</item>
/// </list>
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
        var app = builder.Build();
        var key = app.Configuration[KeySetting];
        if (key is null || key.Length < SeoFetcherProtocol.MinKeyLength)
            throw new InvalidOperationException($"{KeySetting} must hold the shared secret SeoTester calls with, at least {SeoFetcherProtocol.MinKeyLength} characters.");
        // One call's bound; only ever lowered (tests), never raised past the default the app's deadline is built on.
        var timeout = app.Configuration.GetValue(OperationTimeoutSetting, SeoFetchLimits.OperationTimeout);
        if (timeout <= TimeSpan.Zero || timeout > SeoFetchLimits.OperationTimeout)
            timeout = SeoFetchLimits.OperationTimeout;

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

        // Always 200 and depends on nothing (the compose healthcheck): it says the process serves, not that the internet answers.
        app.MapGet(SeoFetcherProtocol.HealthPath, () => Results.Text("ok"));
        app.MapPost(SeoFetcherProtocol.FetchPath, (HttpContext context, ISeoFetcher fetcher) => FetchAsync(context, fetcher, timeout));
        app.MapPost(SeoFetcherProtocol.TlsPath, async (HttpContext context, ISeoFetcher fetcher) =>
        {
            if (await ReadAsync<TlsRequestWire>(context) is not { } request || !SeoFetchLimits.IsHost(request.Host) || request.Port is < 1 or > 65535)
                return Results.BadRequest();
            return await BoundedAsync(context, timeout, token => fetcher.ProbeTlsAsync(request.Host, request.Port, token));
        });
        app.MapPost(SeoFetcherProtocol.DnsPath, async (HttpContext context, ISeoFetcher fetcher) =>
        {
            if (await ReadAsync<DnsRequestWire>(context) is not { } request || !SeoFetchLimits.IsDnsName(request.Name) || !Enum.IsDefined(request.Type))
                return Results.BadRequest();
            return await BoundedAsync(context, timeout, async token => new DnsAnswerWire((await fetcher.QueryDnsAsync(request.Name, request.Type, token))?.ToList()));
        });
        app.MapPost(SeoFetcherProtocol.ZonePath, async (HttpContext context, ISeoFetcher fetcher) =>
        {
            if (await ReadAsync<ZoneRequestWire>(context) is not { } request || !SeoFetchLimits.IsDnsName(request.Host))
                return Results.BadRequest();
            return await BoundedAsync(context, timeout, async token => new ZoneAnswerWire(await fetcher.ZoneApexAsync(request.Host, token)));
        });
        return app;
    }

    private static async Task FetchAsync(HttpContext context, ISeoFetcher fetcher, TimeSpan timeout)
    {
        if (await ReadAsync<FetchRequestWire>(context) is not { } wire
            || SeoFetcherProtocol.HttpUrl(wire.Url) is not { } url
            || wire.Method is null || wire.Client is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var request = new SeoFetchRequest(url, wire.MaxBodyBytes)
        {
            Method = wire.Method,
            Client = wire.Client,
            Headers = wire.Headers?.Select(h => new KeyValuePair<string, string>(h?.Name!, h?.Value!)).ToList(),
            Body = wire.Body,
        };
        if (SeoFetchLimits.Problem(request) is { } problem)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(problem, context.RequestAborted);
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(timeout);
        FetchResult result;
        try
        {
            result = await fetcher.FetchAsync(request, deadline.Token);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            result = new FetchResult(url, null, 0, new Dictionary<string, string>(), [], null, null, [], null, false,
                (int)timeout.TotalMilliseconds, "timed out", null);
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = SeoFetcherProtocol.FramedContentType;
        await SeoFetcherProtocol.WriteFrameAsync(context.Response.Body, SeoFetcherProtocol.ToWire(result), result.Body, context.RequestAborted);
    }

    /// <summary>One call under <see cref="SeoFetchLimits.OperationTimeout"/>; a call that runs out answers 504.</summary>
    private static async Task<IResult> BoundedAsync<T>(HttpContext context, TimeSpan timeout, Func<CancellationToken, Task<T>> call)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(timeout);
        try
        {
            return Results.Json(await call(deadline.Token), SeoFetcherProtocol.Json);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
    }

    /// <summary>The request's JSON, at most <see cref="SeoFetchLimits.MaxApiRequestBytes"/>; null when it is larger, not JSON, or not this shape.</summary>
    private static async Task<T?> ReadAsync<T>(HttpContext context) where T : class
    {
        if (context.Request.ContentLength > SeoFetchLimits.MaxApiRequestBytes)
            return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > SeoFetchLimits.MaxApiRequestBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        try
        {
            return JsonSerializer.Deserialize<T>(buffer.ToArray(), SeoFetcherProtocol.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
