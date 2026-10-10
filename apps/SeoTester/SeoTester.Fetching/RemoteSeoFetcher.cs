using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using SeoTester.Checks;
using SeoTester.Crawler;
using SeoTester.Dns;

namespace SeoTester.Fetching;

/// <summary>Where the app's target traffic runs (<c>SeoTester:Fetcher:Mode</c>, D54).</summary>
public enum SeoFetcherMode
{
    /// <summary>In the app's own process (Development and tests).</summary>
    InProcess,

    /// <summary>In the <c>seo-fetcher</c> container, over its internal API (Production, which refuses anything else).</summary>
    Remote,
}

/// <summary><c>SeoTester:Fetcher</c>: the app's side of the fetcher.</summary>
public sealed class SeoFetcherClientOptions
{
    public const string SectionName = "SeoTester:Fetcher";

    /// <summary>Unset: <see cref="SeoFetcherMode.Remote"/> in Production, <see cref="SeoFetcherMode.InProcess"/> elsewhere.</summary>
    public SeoFetcherMode? Mode { get; set; }

    /// <summary>The fetcher's base address on the internal network, e.g. <c>http://seo-fetcher:8080/</c>.</summary>
    public string? Url { get; set; }

    /// <summary>The shared secret (the fetcher's <c>SeoFetcher:Key</c>). Never logged.</summary>
    public string? Key { get; set; }

    /// <summary>Added to a call's own bound (<see cref="SeoFetchLimits.OperationTimeout"/>) for the hop to the fetcher.</summary>
    public TimeSpan Margin { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// The fetcher container over its internal API (D54). Every answer is read under bounds (frame and JSON sizes, the
/// body no larger than asked for, a deadline) and every field checked before the app sees it
/// (<see cref="SeoFetcherProtocol.ToResult"/>): the fetcher talks to untrusted websites, so what it says is untrusted.
/// </summary>
public sealed class RemoteSeoFetcher(HttpClient http, IOptions<SeoFetcherClientOptions> options) : ISeoFetcher
{
    public async Task<FetchResult> FetchAsync(SeoFetchRequest request, CancellationToken cancellationToken)
    {
        if (SeoFetchLimits.Problem(request) is { } problem)
            return Refused(request, problem);
        var wire = new FetchRequestWire(request.Url.AbsoluteUri, request.Method, request.Client, SeoHttp.ClampBody(request.Client, request.MaxBodyBytes),
            request.Headers?.Select(h => new HeaderWire(h.Key, h.Value)).ToList(), request.Body);
        return await CallAsync(SeoFetcherProtocol.FetchPath, wire, async (response, token) =>
        {
            if (response.StatusCode == HttpStatusCode.BadRequest)
                return Refused(request, "request");
            if (response.Content.Headers.ContentType?.MediaType != SeoFetcherProtocol.FramedContentType)
                throw new SeoFetcherUnavailableException("The fetcher's answer is not a fetch frame.");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var (meta, body) = await SeoFetcherProtocol.ReadFrameAsync(stream, wire.MaxBodyBytes, token);
            return SeoFetcherProtocol.ToResult(request, meta, body);
        }, cancellationToken);
    }

    public Task<TlsFacts> ProbeTlsAsync(string host, int port, CancellationToken cancellationToken)
        => CallAsync(SeoFetcherProtocol.TlsPath, new TlsRequestWire(host, port), async (response, token)
            => SeoFetcherProtocol.Sanitize(await ReadJsonAsync<TlsFacts>(response, token)), cancellationToken);

    public Task<IReadOnlyList<string>?> QueryDnsAsync(string name, SeoDnsType type, CancellationToken cancellationToken)
        => CallAsync(SeoFetcherProtocol.DnsPath, new DnsRequestWire(name, type), async (response, token)
            => SeoFetcherProtocol.Sanitize((await ReadJsonAsync<DnsAnswerWire>(response, token)).Records), cancellationToken);

    public Task<string?> ZoneApexAsync(string host, CancellationToken cancellationToken)
        => CallAsync(SeoFetcherProtocol.ZonePath, new ZoneRequestWire(host), async (response, token)
            => (await ReadJsonAsync<ZoneAnswerWire>(response, token)).Zone is { Length: > 0 and <= 253 } zone && SeoFetchLimits.IsDnsName(zone) ? zone : null,
            cancellationToken);

    private async Task<T> CallAsync<TRequest, T>(string path, TRequest body, Func<HttpResponseMessage, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        var o = options.Value;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SeoFetchLimits.OperationTimeout + o.Margin);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/'))
            {
                Content = JsonContent.Create(body, options: SeoFetcherProtocol.Json),
            };
            request.Headers.TryAddWithoutValidation(SeoFetcherProtocol.KeyHeader, o.Key);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SeoFetcherUnavailableException("The fetcher refused this app's key (SeoTester:Fetcher:Key must equal the fetcher's SeoFetcher:Key).");
            if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.BadRequest)
                throw new SeoFetcherUnavailableException($"The fetcher answered {(int)response.StatusCode}.");
            return await read(response, deadline.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SeoFetcherUnavailableException("The fetcher did not answer in time.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or EndOfStreamException)
        {
            throw new SeoFetcherUnavailableException("The fetcher could not be reached or answered outside its contract.", ex);
        }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.OK)
            throw new SeoFetcherUnavailableException("The fetcher refused the request.");
        if (response.Content.Headers.ContentLength > SeoFetcherProtocol.MaxMetaBytes)
            throw new SeoFetcherUnavailableException("The fetcher's answer is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > SeoFetcherProtocol.MaxMetaBytes)
                throw new SeoFetcherUnavailableException("The fetcher's answer is too large.");
            buffer.Write(chunk, 0, read);
        }
        return System.Text.Json.JsonSerializer.Deserialize<T>(buffer.ToArray(), SeoFetcherProtocol.Json)
            ?? throw new SeoFetcherUnavailableException("The fetcher's answer is empty.");
    }

    private static FetchResult Refused(SeoFetchRequest request, string what)
        => new(request.Url, null, 0, new Dictionary<string, string>(), [], null, null, [], null, false, 0, "refused: " + what, null);
}
