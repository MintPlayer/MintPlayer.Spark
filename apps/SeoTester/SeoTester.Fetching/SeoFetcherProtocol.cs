using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SeoTester.Checks;
using SeoTester.Crawler;
using SeoTester.Dns;
using SeoTester.Tls;

namespace SeoTester.Fetching;

/// <summary>
/// The fetcher's internal API (<c>docs/seo_analyzer_PRD.md</c> D54). Every call but <see cref="HealthPath"/> carries
/// the shared secret in <see cref="KeyHeader"/>; JSON in, JSON out, except a fetch's answer, which is framed
/// (<see cref="WriteFrameAsync"/>) so a body travels as bytes, not base64.
/// </summary>
public static partial class SeoFetcherProtocol
{
    public const string KeyHeader = "X-Seo-Fetcher-Key";
    public const string HealthPath = "/health";
    public const string FetchPath = "/fetch";
    public const string TlsPath = "/tls";
    public const string DnsPath = "/dns";
    public const string ZonePath = "/dns/zone";

    /// <summary>A fetch's answer: 4 bytes (big-endian) of metadata length, the metadata as JSON, then the body.</summary>
    public const string FramedContentType = "application/vnd.seotester.fetch";

    /// <summary>Most bytes of a fetch answer's metadata, and of any JSON answer.</summary>
    public const int MaxMetaBytes = 256 * 1024;

    /// <summary>Shortest shared secret either side accepts: 32 characters (e.g. <c>openssl rand -hex 32</c> gives 64).</summary>
    public const int MinKeyLength = 32;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 16,
    };

    /// <summary>
    /// Constant-time: both sides hashed to 32 bytes first, so neither the length nor a common prefix of the presented
    /// key shows in the time taken.
    /// </summary>
    public static bool KeyMatches(string? presented, string expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
            return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }

    /// <summary>Writes a fetch's framed answer.</summary>
    public static async Task WriteFrameAsync(Stream output, FetchAnswerWire meta, byte[]? body, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(meta, Json);
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, json.Length);
        await output.WriteAsync(length, cancellationToken);
        await output.WriteAsync(json, cancellationToken);
        if (body is { Length: > 0 })
            await output.WriteAsync(body, cancellationToken);
    }

    /// <summary>
    /// Reads a framed answer, refusing one that breaks the frame's limits: metadata over <see cref="MaxMetaBytes"/> or a
    /// body over <paramref name="maxBodyBytes"/>. Untrusted: the caller still checks every field (<see cref="ToResult"/>).
    /// </summary>
    public static async Task<(FetchAnswerWire Meta, byte[] Body)> ReadFrameAsync(Stream input, int maxBodyBytes, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        await input.ReadExactlyAsync(length, cancellationToken);
        var metaLength = BinaryPrimitives.ReadInt32BigEndian(length);
        if (metaLength is <= 0 or > MaxMetaBytes)
            throw new SeoFetcherUnavailableException("The fetcher's answer has no valid frame.");
        var json = new byte[metaLength];
        await input.ReadExactlyAsync(json, cancellationToken);
        var meta = JsonSerializer.Deserialize<FetchAnswerWire>(json, Json) ?? throw new SeoFetcherUnavailableException("The fetcher's answer has no metadata.");

        using var body = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (body.Length + read > maxBodyBytes)
                throw new SeoFetcherUnavailableException("The fetcher's answer is larger than asked for.");
            body.Write(chunk, 0, read);
        }
        return (meta, body.ToArray());
    }

    /// <summary>The wire form of a result (the fetcher's side).</summary>
    public static FetchAnswerWire ToWire(FetchResult result) => new(
        result.FinalUrl?.AbsoluteUri, result.StatusCode, new Dictionary<string, string>(result.Headers), result.SetCookies.ToList(), result.WireEncoding,
        result.HttpVersion?.ToString(), result.Hops.Select(h => new HopWire(h.Url, h.Status)).ToList(), result.Body is not null, result.Truncated,
        result.ElapsedMs, result.Error, result.RetryAfter is { } wait ? (long)wait.TotalMilliseconds : null);

    /// <summary>
    /// A result from the fetcher's answer (the app's side), every field checked or bounded: the answer is as untrusted as
    /// the website behind it (a compromised fetcher must not be able to hand the app more than a website could).
    /// </summary>
    public static FetchResult ToResult(SeoFetchRequest request, FetchAnswerWire meta, byte[] body)
    {
        if (meta.Status is not 0 and (< 100 or > 599))
            throw new SeoFetcherUnavailableException("The fetcher's answer has an impossible status.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in (meta.Headers ?? []).Take(SeoFetchLimits.MaxHeaders * 2))
            if (HeaderName().IsMatch(name ?? "") && value is not null)
                headers[name!.ToLowerInvariant()] = Cut(value, SeoFetchLimits.MaxHeaderValue * 2);
        var hops = (meta.Hops ?? []).Take(20)
            .Where(h => h is not null && h.Status is >= 300 and < 400 && HttpUrl(h.Url) is not null)
            .Select(h => (HttpUrl(h.Url)!.AbsoluteUri, h.Status)).ToList();
        Version? version = meta.HttpVersion is { } v && Version.TryParse(v, out var parsed) && parsed.Major is >= 1 and <= 3 ? parsed : null;
        TimeSpan? retryAfter = meta.RetryAfterMs is { } ms ? TimeSpan.FromMilliseconds(Math.Clamp(ms, 0, (long)TimeSpan.FromDays(1).TotalMilliseconds)) : null;
        return new FetchResult(request.Url, HttpUrl(meta.FinalUrl), meta.Status, headers,
            (meta.SetCookies ?? []).Where(c => c is not null).Take(50).Select(c => Cut(c, SeoFetchLimits.MaxHeaderValue)).ToList(),
            meta.WireEncoding is { } encoding ? Cut(encoding, 100) : null, version, hops, meta.HasBody ? body : null, meta.Truncated,
            Math.Clamp(meta.ElapsedMs, 0, 600_000), meta.Error is { } error ? Cut(error, 200) : null, retryAfter);
    }

    /// <summary>TLS facts from the fetcher, strings bounded and enums checked.</summary>
    public static TlsFacts Sanitize(TlsFacts facts)
    {
        static string? C(string? s) => s is null ? null : Cut(s, 300);
        static LegacyTlsAnswer? A(LegacyTlsAnswer? a) => a is { } x && !Enum.IsDefined(x) ? LegacyTlsAnswer.Inconclusive : a;
        return facts with
        {
            Error = C(facts.Error), Protocol = C(facts.Protocol), Alpn = C(facts.Alpn), CertificateProblem = C(facts.CertificateProblem),
            CertificateIssuer = C(facts.CertificateIssuer), Tls10 = A(facts.Tls10), Tls11 = A(facts.Tls11),
        };
    }

    /// <summary>DNS records from the fetcher: at most 100, each at most 4 096 characters.</summary>
    public static IReadOnlyList<string>? Sanitize(List<string>? records)
        => records?.Where(r => r is not null).Take(100).Select(r => Cut(r, 4096)).ToList();

    internal static Uri? HttpUrl(string? value)
        => value is { Length: > 0 and <= SeoFetchLimits.MaxUrlLength } && Uri.TryCreate(value, UriKind.Absolute, out var url)
           && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) ? url : null;

    private static string Cut(string value, int max) => value.Length > max ? value[..max] : value;

    /// <summary>An RFC 9110 token.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,128}$")]
    internal static partial Regex HeaderName();
}

/// <summary>The bounds the fetcher's API holds a caller to (D54), the same in both modes where they apply.</summary>
public static partial class SeoFetchLimits
{
    /// <summary>Largest request body passed on (a form post or an API call of a rendered page's script).</summary>
    public const int MaxRequestBodyBytes = 1024 * 1024;

    /// <summary>Largest request the fetcher's API reads: the body above, base64, plus headers and the URL.</summary>
    public const int MaxApiRequestBytes = 2 * 1024 * 1024;

    public const int MaxUrlLength = 8192;
    public const int MaxHeaders = 100;
    public const int MaxHeaderValue = 8 * 1024;

    /// <summary>The longest one call may take on the fetcher: PageSpeed Insights' 100 s client timeout plus room.</summary>
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(120);

    private static readonly HashSet<string> CrawlerMethods = new(StringComparer.Ordinal) { "GET", "HEAD" };
    private static readonly HashSet<string> BrowserMethods = new(StringComparer.Ordinal) { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" };

    /// <summary>Why <paramref name="request"/> may not be sent, or null.</summary>
    public static string? Problem(SeoFetchRequest request)
    {
        if (!request.Url.IsAbsoluteUri || (request.Url.Scheme != Uri.UriSchemeHttp && request.Url.Scheme != Uri.UriSchemeHttps))
            return "url";
        if (request.Url.AbsoluteUri.Length > MaxUrlLength || request.Url.UserInfo.Length > 0)
            return "url";
        if (!SeoHttp.MaxBodyBytes.ContainsKey(request.Client))
            return "client";
        if (!(request.Client == SeoHttp.Browser ? BrowserMethods : CrawlerMethods).Contains(request.Method))
            return "method";
        if (request.MaxBodyBytes < 0)
            return "size";
        if (request.Body is { Length: > MaxRequestBodyBytes })
            return "body";
        if (request.Body is { Length: > 0 } && request.Client != SeoHttp.Browser)
            return "body";
        var headers = request.Headers ?? [];
        if (headers.Count > MaxHeaders)
            return "headers";
        foreach (var (name, value) in headers)
            if (name is null || !SeoFetcherProtocol.HeaderName().IsMatch(name) || value is null || value.Length > MaxHeaderValue || value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
                return "headers";
        return null;
    }

    /// <summary>A host name or address literal the TLS probe may be asked about.</summary>
    public static bool IsHost(string? host) => host is { Length: > 0 and <= 253 } && Host().IsMatch(host);

    /// <summary>A DNS name SeoTester asks about (labels, underscores for <c>_dmarc</c> and <c>_seotester</c>).</summary>
    public static bool IsDnsName(string? name) => name is { Length: > 0 and <= 253 } && DnsName().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9.\-:\[\]]+$")]
    private static partial Regex Host();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex DnsName();
}

public sealed record HeaderWire(string Name, string Value);

public sealed record HopWire(string Url, int Status);

public sealed record FetchRequestWire(string Url, string Method, string Client, int MaxBodyBytes, List<HeaderWire>? Headers, byte[]? Body);

public sealed record FetchAnswerWire(
    string? FinalUrl, int Status, Dictionary<string, string>? Headers, List<string>? SetCookies, string? WireEncoding, string? HttpVersion,
    List<HopWire>? Hops, bool HasBody, bool Truncated, int ElapsedMs, string? Error, long? RetryAfterMs);

public sealed record TlsRequestWire(string Host, int Port);

public sealed record DnsRequestWire(string Name, SeoDnsType Type);

public sealed record DnsAnswerWire(List<string>? Records);

public sealed record ZoneRequestWire(string Host);

public sealed record ZoneAnswerWire(string? Zone);
