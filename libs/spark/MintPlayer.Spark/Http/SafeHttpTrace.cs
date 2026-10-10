using System.Net;

namespace MintPlayer.Spark.Http;

/// <summary>
/// What the SSRF-safe client did on one request that a caller cannot see on the response: the redirects it followed
/// and the <c>Content-Encoding</c> the server sent before the client decompressed the body. Opt-in per request, so a
/// request without one behaves exactly as before:
/// <code>
/// var trace = SafeHttpTrace.Attach(request);
/// using var response = await client.SendAsync(request, cancellationToken);
/// // trace.Redirects: every hop, in order; trace.ContentEncoding: "br", "gzip", … or null
/// </code>
/// A crawler needs both: a redirect chain is a finding (<c>docs/seo_analyzer_PRD.md</c> §3.1), and the decompressed
/// response no longer says how it travelled.
/// </summary>
public sealed class SafeHttpTrace
{
    /// <summary>The request option the handler looks for.</summary>
    public static readonly HttpRequestOptionsKey<SafeHttpTrace> Key = new("MintPlayer.Spark.Http.SafeHttpTrace");

    /// <summary>Each redirect followed: the URI that answered and its status, in order. Empty when none was.</summary>
    public List<SafeHttpHop> Redirects { get; } = [];

    /// <summary>The final response's <c>Content-Encoding</c> as sent, before decompression; null when there was none.</summary>
    public string? ContentEncoding { get; internal set; }

    /// <summary>Attaches a new trace to <paramref name="request"/> and returns it.</summary>
    public static SafeHttpTrace Attach(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var trace = new SafeHttpTrace();
        request.Options.Set(Key, trace);
        return trace;
    }
}

/// <summary>One redirect: <paramref name="Uri"/> answered <paramref name="Status"/> with a <c>Location</c> to <paramref name="Location"/>.</summary>
public sealed record SafeHttpHop(Uri Uri, HttpStatusCode Status, Uri Location);
