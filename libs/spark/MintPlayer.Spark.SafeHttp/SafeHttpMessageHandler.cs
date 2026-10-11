using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;

namespace MintPlayer.Spark.Http;

/// <summary>
/// The outer handler of the SSRF-safe client:
/// <list type="bullet">
/// <item>checks the scheme and port of the request and of every redirect before it is sent;</item>
/// <item>follows redirects itself (the primary handler has <c>AllowAutoRedirect = false</c>), at most
/// <see cref="SafeHttpOptions.MaxRedirects"/>, dropping <c>Authorization</c> and <c>Cookie</c> when the
/// origin changes, or refusing any origin change under <see cref="SafeHttpOptions.RedirectsWithinOriginOnly"/>
/// (compared with the origin first requested). The address of each hop is checked by <see cref="SafeHttpConnector"/> when it connects;</item>
/// <item>decompresses itself, so the wire bytes and the decompressed bytes are each capped while they stream;</item>
/// <item>surfaces a <see cref="SafeHttpBlockedException"/> raised in the connect callback as itself, not
/// wrapped in the <see cref="HttpRequestException"/> the sockets handler puts around it.</item>
/// </list>
/// </summary>
internal sealed class SafeHttpMessageHandler(SafeHttpPolicy policy, SafeHttpOptions options) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var current = request;
        request.Options.TryGetValue(SafeHttpTrace.Key, out var trace);
        for (var redirects = 0; ; redirects++)
        {
            policy.CheckUri(current.RequestUri);
            if (current.Headers.AcceptEncoding.Count == 0)
            {
                current.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
                current.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
                current.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
            }

            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(current, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex is not SafeHttpBlockedException && FindBlocked(ex) is { } blocked)
            {
                ExceptionDispatchInfo.Throw(blocked);
                throw;
            }

            if (!options.FollowRedirects || !IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
            {
                if (trace is not null)
                    trace.ContentEncoding = response.Content.Headers.ContentEncoding.Count > 0 ? string.Join(", ", response.Content.Headers.ContentEncoding) : null;
                return await CapAsync(response, cancellationToken);
            }

            response.Dispose();
            var target = new Uri(current.RequestUri!, location);
            trace?.Redirects.Add(new SafeHttpHop(current.RequestUri!, response.StatusCode, target));
            if (redirects >= options.MaxRedirects)
                throw new SafeHttpBlockedException(SafeHttpBlockReason.TooManyRedirects);

            if (options.RedirectsWithinOriginOnly && !SameOrigin(request.RequestUri, target))
                throw new SafeHttpBlockedException(SafeHttpBlockReason.CrossOriginRedirect);

            current = NextHop(current, target, response.StatusCode);
        }
    }

    private static SafeHttpBlockedException? FindBlocked(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is SafeHttpBlockedException blocked)
                return blocked;
        return null;
    }

    /// <summary>Scheme, host and port equal (<see cref="UriComponents.SchemeAndServer"/> includes the port, default ports normalised).</summary>
    private static bool SameOrigin(Uri? a, Uri b)
        => Uri.Compare(a, b, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>The request for the next hop, with the method and body rules browsers apply (RFC 9110 §15.4).</summary>
    private static HttpRequestMessage NextHop(HttpRequestMessage previous, Uri target, HttpStatusCode status)
    {
        var method = previous.Method;
        var content = previous.Content;
        if ((status == HttpStatusCode.SeeOther && method != HttpMethod.Head)
            || (status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found && method == HttpMethod.Post))
        {
            method = HttpMethod.Get;
            content = null;
        }

        var next = new HttpRequestMessage(method, target)
        {
            Version = previous.Version,
            VersionPolicy = previous.VersionPolicy,
            Content = content,
        };
        var sameOrigin = SameOrigin(previous.RequestUri, target);
        foreach (var header in previous.Headers)
        {
            if (!sameOrigin && (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                continue;
            next.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return next;
    }

    /// <summary>Replaces the body with one that enforces both byte caps while it is read.</summary>
    private async Task<HttpResponseMessage> CapAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var original = response.Content;
        if (original.Headers.ContentLength > options.MaxResponseBytes)
        {
            response.Dispose();
            throw new SafeHttpBlockedException(SafeHttpBlockReason.ResponseTooLarge);
        }

        Stream body = new CappedStream(await original.ReadAsStreamAsync(cancellationToken), options.MaxResponseBytes, SafeHttpBlockReason.ResponseTooLarge);
        var encodings = original.Headers.ContentEncoding;
        var decompressed = encodings.Count == 1 ? Decompress(encodings.First(), body) : null;
        if (decompressed is not null)
            body = new CappedStream(decompressed, options.MaxDecompressedBytes, SafeHttpBlockReason.DecompressedTooLarge);

        var capped = new StreamContent(body);
        foreach (var header in original.Headers)
        {
            if (decompressed is not null && (header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
                continue;
            capped.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        // The original content is not disposed: its stream is now owned by the capped body, and
        // disposing that releases the connection.
        response.Content = capped;
        return response;
    }

    private static Stream? Decompress(string encoding, Stream body) => encoding.ToLowerInvariant() switch
    {
        "gzip" or "x-gzip" => new GZipStream(body, CompressionMode.Decompress),
        // HTTP "deflate" is the zlib format (RFC 9110 §8.4.1.2), as SocketsHttpHandler reads it.
        "deflate" => new ZLibStream(body, CompressionMode.Decompress),
        "br" => new BrotliStream(body, CompressionMode.Decompress),
        _ => null,
    };
}

/// <summary>A read-only stream that throws <see cref="SafeHttpBlockedException"/> once more than a set number of bytes is read.</summary>
internal sealed class CappedStream(Stream inner, long limit, SafeHttpBlockReason reason) : Stream
{
    private long read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => Count(await inner.ReadAsync(buffer, cancellationToken));

    private int Count(int n)
    {
        read += n;
        if (read > limit)
            throw new SafeHttpBlockedException(reason);
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
