using SeoTester.Checks;
using SeoTester.Crawler;
using SeoTester.Dns;

namespace SeoTester.Fetching;

/// <summary>One HTTP request to a target, as the crawl pipeline, the browser router, verification and the og-image proxy ask for it.</summary>
/// <param name="Url">An absolute http(s) URL.</param>
/// <param name="MaxBodyBytes">Most body bytes kept; more sets <see cref="FetchResult.Truncated"/>. Cut to the client's cap (<see cref="SeoHttp.MaxBodyBytes"/>).</param>
public sealed record SeoFetchRequest(Uri Url, int MaxBodyBytes)
{
    /// <summary>The HTTP method: GET or HEAD, or for the browser's client any the browser sends.</summary>
    public string Method { get; init; } = "GET";

    /// <summary>The named SSRF-safe client, one of <see cref="SeoHttp.MaxBodyBytes"/>'s keys.</summary>
    public string Client { get; init; } = SeoHttp.Crawler;

    /// <summary>Extra request headers (an API key travels here, never in the URL), or the browser's headers.</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Headers { get; init; }

    /// <summary>A request body (the browser's form posts and API calls), at most <see cref="SeoFetchLimits.MaxRequestBodyBytes"/>.</summary>
    public byte[]? Body { get; init; }
}

/// <summary>
/// Everything SeoTester asks of the internet on a user's behalf (<c>docs/seo_analyzer_PRD.md</c> D54): HTTP through
/// the SSRF-safe clients, the TLS probes and DNS. Two implementations:
/// <list type="bullet">
/// <item><see cref="LocalSeoFetcher"/>: in this process (Development, tests, and inside the fetcher container);</item>
/// <item><see cref="RemoteSeoFetcher"/>: the <c>seo-fetcher</c> container over its internal API (Production), so the
/// process that holds the database connection never opens a connection to a target.</item>
/// </list>
/// Neither throws for what a target does (a failed fetch is a <see cref="FetchResult"/> with status 0); the remote one
/// throws <see cref="SeoFetcherUnavailableException"/> when the fetcher itself cannot be used.
/// </summary>
public interface ISeoFetcher
{
    Task<FetchResult> FetchAsync(SeoFetchRequest request, CancellationToken cancellationToken);

    /// <summary>The TLS facts of <paramref name="host"/>:<paramref name="port"/> (<see cref="ITlsProber"/>).</summary>
    Task<TlsFacts> ProbeTlsAsync(string host, int port, CancellationToken cancellationToken);

    /// <summary>As <see cref="ISeoDns.QueryAsync"/>: empty when the name has none, null when the lookup failed.</summary>
    Task<IReadOnlyList<string>?> QueryDnsAsync(string name, SeoDnsType type, CancellationToken cancellationToken);

    /// <summary>As <see cref="ISeoDns.ZoneApexAsync"/>.</summary>
    Task<string?> ZoneApexAsync(string host, CancellationToken cancellationToken);
}

/// <summary>The fetcher could not be used (unreachable, refused our key, or answered outside its contract). Never the target's fault.</summary>
public sealed class SeoFetcherUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The shapes callers had before D54, over <see cref="ISeoFetcher"/>.</summary>
public static class SeoFetcherExtensions
{
    /// <param name="requestHeaders">Extra request headers (an API key: sent in a header, never in the URL, so no log shows it).</param>
    public static Task<FetchResult> FetchAsync(this ISeoFetcher fetcher, Uri url, int maxBodyBytes, CancellationToken cancellationToken,
        HttpMethod? method = null, string client = SeoHttp.Crawler, IReadOnlyDictionary<string, string>? requestHeaders = null)
        => fetcher.FetchAsync(new SeoFetchRequest(url, maxBodyBytes)
        {
            Method = (method ?? HttpMethod.Get).Method,
            Client = client,
            Headers = requestHeaders?.ToList(),
        }, cancellationToken);

    /// <summary>The TXT records at <paramref name="name"/>; empty when there are none or the lookup failed.</summary>
    public static async Task<IReadOnlyList<string>> GetTxtRecordsAsync(this ISeoFetcher fetcher, string name, CancellationToken cancellationToken)
        => await fetcher.QueryDnsAsync(name, SeoDnsType.Txt, cancellationToken) ?? [];
}

/// <summary>In this process: the SSRF-safe clients (<see cref="PageFetcher"/>), <see cref="ITlsProber"/> and <see cref="ISeoDns"/>.</summary>
public sealed class LocalSeoFetcher(PageFetcher pages, ITlsProber tls, ISeoDns dns) : ISeoFetcher
{
    public Task<FetchResult> FetchAsync(SeoFetchRequest request, CancellationToken cancellationToken) => pages.SendAsync(request, cancellationToken);

    public Task<TlsFacts> ProbeTlsAsync(string host, int port, CancellationToken cancellationToken) => tls.ProbeAsync(host, port, cancellationToken);

    public Task<IReadOnlyList<string>?> QueryDnsAsync(string name, SeoDnsType type, CancellationToken cancellationToken) => dns.QueryAsync(name, type, cancellationToken);

    public Task<string?> ZoneApexAsync(string host, CancellationToken cancellationToken) => dns.ZoneApexAsync(host, cancellationToken);
}
