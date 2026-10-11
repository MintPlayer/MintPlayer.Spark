using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using SeoTester.Crawler;
using SeoTester.Dns;
using SeoTester.Fetching;

namespace SeoTester.Fetcher.Endpoints;

// The fetcher's internal API (docs/seo_analyzer_PRD.md D54). The key check runs before any of these (FetcherHost.Build),
// so a binder only ever reads the body of a call that carries the key. Every binder reads at most
// SeoFetchLimits.MaxApiRequestBytes (FetcherRequestBody) and every refusal is a bare 400, as the app's RemoteSeoFetcher
// expects.

/// <summary><c>GET /health</c>: always 200 and depends on nothing (the compose healthcheck): it says the process serves, not that the internet answers.</summary>
internal sealed class FetcherHealthEndpoint : IGetEndpoint
{
    public static string Path => SeoFetcherProtocol.HealthPath;

    public Task<IResult> HandleAsync(HttpContext httpContext) => Task.FromResult(Results.Text("ok"));
}

/// <summary><c>POST /fetch</c>: one HTTP fetch through the SSRF-safe clients, answered as a frame (meta JSON, then the body's bytes).</summary>
internal sealed partial class FetcherFetchEndpoint : IPostEndpoint<FetchRequestWire>
{
    public static string Path => SeoFetcherProtocol.FetchPath;

    [Inject] private readonly ISeoFetcher fetcher;
    [Inject] private readonly FetcherCallBound bound;

    protected override async ValueTask<FetchRequestWire?> BindRequestAsync(HttpContext context)
        => await FetcherRequestBody.ReadAsync<FetchRequestWire>(context.Request.Body, context.Request.ContentLength, context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(Results.BadRequest());

    public override async Task<IResult> HandleAsync(FetchRequestWire wire, CancellationToken cancellationToken)
    {
        if (SeoFetcherProtocol.HttpUrl(wire.Url) is not { } url || wire.Method is null || wire.Client is null)
            return Results.BadRequest();
        var request = new SeoFetchRequest(url, wire.MaxBodyBytes)
        {
            Method = wire.Method,
            Client = wire.Client,
            Headers = wire.Headers?.Select(h => new KeyValuePair<string, string>(h?.Name!, h?.Value!)).ToList(),
            Body = wire.Body,
        };
        if (SeoFetchLimits.Problem(request) is { } problem)
            return Results.Text(problem, statusCode: StatusCodes.Status400BadRequest);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(bound.Timeout);
        FetchResult result;
        try
        {
            result = await fetcher.FetchAsync(request, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = new FetchResult(url, null, 0, new Dictionary<string, string>(), [], null, null, [], null, false,
                (int)bound.Timeout.TotalMilliseconds, "timed out", null);
        }
        return new FetchFrameResult(result);
    }
}

/// <summary><c>POST /tls</c>: the TLS probes of one host and port.</summary>
internal sealed partial class FetcherTlsEndpoint : IPostEndpoint<TlsRequestWire>
{
    public static string Path => SeoFetcherProtocol.TlsPath;

    [Inject] private readonly ISeoFetcher fetcher;
    [Inject] private readonly FetcherCallBound bound;

    protected override async ValueTask<TlsRequestWire?> BindRequestAsync(HttpContext context)
        => await FetcherRequestBody.ReadAsync<TlsRequestWire>(context.Request.Body, context.Request.ContentLength, context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(Results.BadRequest());

    public override Task<IResult> HandleAsync(TlsRequestWire request, CancellationToken cancellationToken)
    {
        if (!SeoFetchLimits.IsHost(request.Host) || request.Port is < 1 or > 65535)
            return Task.FromResult(Results.BadRequest());
        return bound.JsonAsync(token => fetcher.ProbeTlsAsync(request.Host, request.Port, token), cancellationToken);
    }
}

/// <summary><c>POST /dns</c>: one DNS query.</summary>
internal sealed partial class FetcherDnsEndpoint : IPostEndpoint<DnsRequestWire>
{
    public static string Path => SeoFetcherProtocol.DnsPath;

    [Inject] private readonly ISeoFetcher fetcher;
    [Inject] private readonly FetcherCallBound bound;

    protected override async ValueTask<DnsRequestWire?> BindRequestAsync(HttpContext context)
        => await FetcherRequestBody.ReadAsync<DnsRequestWire>(context.Request.Body, context.Request.ContentLength, context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(Results.BadRequest());

    public override Task<IResult> HandleAsync(DnsRequestWire request, CancellationToken cancellationToken)
    {
        if (!SeoFetchLimits.IsDnsName(request.Name) || !Enum.IsDefined(request.Type))
            return Task.FromResult(Results.BadRequest());
        return bound.JsonAsync(async token => new DnsAnswerWire((await fetcher.QueryDnsAsync(request.Name, request.Type, token))?.ToList()), cancellationToken);
    }
}

/// <summary><c>POST /dns/zone</c>: the zone apex of a host.</summary>
internal sealed partial class FetcherZoneEndpoint : IPostEndpoint<ZoneRequestWire>
{
    public static string Path => SeoFetcherProtocol.ZonePath;

    [Inject] private readonly ISeoFetcher fetcher;
    [Inject] private readonly FetcherCallBound bound;

    protected override async ValueTask<ZoneRequestWire?> BindRequestAsync(HttpContext context)
        => await FetcherRequestBody.ReadAsync<ZoneRequestWire>(context.Request.Body, context.Request.ContentLength, context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(Results.BadRequest());

    public override Task<IResult> HandleAsync(ZoneRequestWire request, CancellationToken cancellationToken)
    {
        if (!SeoFetchLimits.IsDnsName(request.Host))
            return Task.FromResult(Results.BadRequest());
        return bound.JsonAsync(async token => new ZoneAnswerWire(await fetcher.ZoneApexAsync(request.Host, token)), cancellationToken);
    }
}
