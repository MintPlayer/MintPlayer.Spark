using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MintPlayer.Spark.Http;

namespace SeoTester.Crawler;

/// <summary>One fetch of a target URL: what the checks need of the response, the body capped.</summary>
/// <param name="FinalUrl">The URL that answered after redirects; null when the request failed.</param>
/// <param name="StatusCode">0 when no response came (network error, refused by the SSRF rules).</param>
/// <param name="Hops">Each redirect followed: the URL and its status.</param>
/// <param name="RetryAfter">A 429/503's <c>Retry-After</c>, when it had one.</param>
public sealed record FetchResult(
    Uri Requested, Uri? FinalUrl, int StatusCode, IReadOnlyDictionary<string, string> Headers, IReadOnlyList<string> SetCookies,
    string? WireEncoding, Version? HttpVersion, IReadOnlyList<(string Url, int Status)> Hops, byte[]? Body, bool Truncated, int ElapsedMs,
    string? Error, TimeSpan? RetryAfter)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public string? ContentType => Header("content-type");

    public bool IsHtml => ContentType is { } t && (t.Contains("text/html", StringComparison.OrdinalIgnoreCase) || t.Contains("application/xhtml", StringComparison.OrdinalIgnoreCase));

    public bool Ok => StatusCode is >= 200 and < 300;

    /// <summary>The body as text: the charset of <c>Content-Type</c>, else a BOM, else a <c>&lt;meta charset&gt;</c> in the first 1024 bytes, else UTF-8.</summary>
    public string? Text()
    {
        if (Body is null)
            return null;
        var encoding = EncodingFromContentType(ContentType) ?? EncodingFromBom(Body) ?? EncodingFromMeta(Body) ?? Encoding.UTF8;
        try
        {
            return encoding.GetString(Body);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.UTF8.GetString(Body);
        }
    }

    /// <summary>The first 1024 bytes as Latin-1 text: enough to find an early <c>&lt;meta charset&gt;</c>.</summary>
    public string? Prefix => Body is null ? null : Encoding.Latin1.GetString(Body, 0, Math.Min(1024, Body.Length));

    private static Encoding? EncodingFromContentType(string? contentType)
    {
        if (contentType is null || !MediaTypeHeaderValue.TryParse(contentType, out var media) || media.CharSet is not { Length: > 0 } charset)
            return null;
        return TryGet(charset.Trim('"'));
    }

    private static Encoding? EncodingFromBom(byte[] body)
        => body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF ? Encoding.UTF8
            : body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE ? Encoding.Unicode
            : body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF ? Encoding.BigEndianUnicode
            : null;

    private static Encoding? EncodingFromMeta(byte[] body)
    {
        var prefix = Encoding.Latin1.GetString(body, 0, Math.Min(1024, body.Length));
        var index = prefix.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;
        var name = new string(prefix[(index + 8)..].TrimStart('"', '\'').TakeWhile(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return TryGet(name);
    }

    private static Encoding? TryGet(string name)
    {
        try
        {
            return name.Length == 0 ? null : Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Fetches target URLs for the crawl pipeline through the SSRF-safe client (<c>docs/seo_analyzer_PRD.md</c> X2, §6):
/// every hop re-checked, our user agent, no cookies, the body read up to a cap, the redirect chain and the wire
/// encoding traced (<see cref="SafeHttpTrace"/>). It never throws for what the target does; a failure is a
/// <see cref="FetchResult"/> with status 0 and an error that names no internal detail.
/// </summary>
public sealed class PageFetcher(IHttpClientFactory httpClients, ILogger<PageFetcher> logger)
{
    /// <summary>Most bytes of an HTML page kept (HtmlPage parses at most 2 Mi characters).</summary>
    public const int MaxPageBytes = 3 * 1024 * 1024;

    public async Task<FetchResult> FetchAsync(Uri url, int maxBodyBytes, CancellationToken cancellationToken, HttpMethod? method = null, string client = SeoHttp.Crawler)
    {
        var stopwatch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, url)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.UserAgent.ParseAdd(SeoTesterBot.UserAgent);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.AcceptEncoding.ParseAdd("br");
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        request.Headers.AcceptEncoding.ParseAdd("deflate");
        var trace = SafeHttpTrace.Attach(request);
        try
        {
            using var response = await httpClients.CreateClient(client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var cookies = new List<string>();
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    cookies.AddRange(header.Value.Take(50));
                else
                    headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
            }

            byte[]? body = null;
            var truncated = false;
            if (request.Method != HttpMethod.Head)
                (body, truncated) = await ReadCappedAsync(response.Content, maxBodyBytes, cancellationToken);

            return new FetchResult(url, response.RequestMessage?.RequestUri ?? url, (int)response.StatusCode, headers, cookies, trace.ContentEncoding,
                response.Version, Hops(trace), body, truncated, (int)stopwatch.ElapsedMilliseconds, null, RetryAfterOf(response));
        }
        catch (Exception ex) when (ex is HttpRequestException or SafeHttpBlockedException or IOException or TaskCanceledException or TimeoutException
                                   && !cancellationToken.IsCancellationRequested)
        {
            var reason = ex switch
            {
                SafeHttpBlockedException blocked => "refused: " + blocked.Reason,
                TaskCanceledException or TimeoutException => "timed out",
                _ => "no answer",
            };
            logger.LogDebug("Fetch of {Url} failed: {Reason}", url, reason);
            return new FetchResult(url, null, 0, new Dictionary<string, string>(), [], trace.ContentEncoding, null, Hops(trace), null, false,
                (int)stopwatch.ElapsedMilliseconds, reason, null);
        }
    }

    private static IReadOnlyList<(string Url, int Status)> Hops(SafeHttpTrace trace)
        => trace.Redirects.Select(h => (h.Uri.AbsoluteUri, (int)h.Status)).ToList();

    private static async Task<(byte[] Body, bool Truncated)> ReadCappedAsync(HttpContent content, int max, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (buffer.Length < max)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, max - buffer.Length)), cancellationToken);
            if (read == 0)
                return (buffer.ToArray(), false);
            buffer.Write(chunk, 0, read);
        }
        return (buffer.ToArray(), true);
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) || response.Headers.RetryAfter is not { } retry)
            return null;
        if (retry.Delta is { } delta)
            return delta;
        if (retry.Date is { } date)
            return date - DateTimeOffset.UtcNow is { } wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        return null;
    }
}

/// <summary>The named SSRF-safe clients of the crawl pipeline.</summary>
public static class SeoHttp
{
    /// <summary>Every request to a site being audited.</summary>
    public const string Crawler = "SeoTester.Crawler";

    /// <summary>Third-party registries and lists: RDAP and its IANA bootstrap, hstspreload.org.</summary>
    public const string Lookups = "SeoTester.Lookups";

    /// <summary>Name used for raw TLS connections (the TLS probes), under the crawler client's rules.</summary>
    public const string Tls = Crawler;
}
