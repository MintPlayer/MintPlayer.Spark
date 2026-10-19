using System.Text;
using Microsoft.Extensions.Primitives;
using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.Webhooks;

/// <summary>
/// One inbound webhook POST as it arrived: its headers and the <b>exact bytes</b> of its body.
/// <para>
/// The body is never parsed into an object model and written back. A provider signs the bytes it
/// sends, and any round trip through a JSON object model changes them: numbers (<c>1.50</c> →
/// <c>1.5</c>), escapes (<c>\/</c> → <c>/</c>) and, with Newtonsoft's default date handling, even
/// the offset of a timestamp, which then depends on the host's time zone. A signature computed over
/// re-serialized text fails, and the delivery is dropped as forged. So the bytes read from the
/// request are the bytes verified, the bytes enqueued and the bytes a handler reads.
/// </para>
/// </summary>
public sealed class WebhookRequest
{
    private const int ChunkSize = 16 * 1024;

    private readonly Dictionary<string, string> headers;
    private string? bodyText;

    /// <summary>A request with the given headers and body bytes. The bytes are kept as given, not copied.</summary>
    public WebhookRequest(IEnumerable<KeyValuePair<string, StringValues>> headers, byte[] body)
    {
        this.headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
            this.headers[name] = value.ToString();
        Body = body;
    }

    /// <summary>
    /// The request headers, case-insensitive. A header sent more than once appears once, its values
    /// joined with a comma (as <see cref="StringValues.ToString"/> joins them).
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers => headers;

    /// <summary>The body exactly as it was received. Verify and handle these bytes; never re-serialize them.</summary>
    public byte[] Body { get; }

    /// <summary>The <c>Content-Type</c> header, if any.</summary>
    public string? ContentType => Header("Content-Type");

    /// <summary>The value of the header <paramref name="name"/> (case-insensitive), or <see langword="null"/>.</summary>
    public string? Header(string name) => headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// The body decoded as UTF-8, for handlers that need text. Decoding is one-way: verify against
    /// <see cref="Body"/>, never against the bytes of this string.
    /// </summary>
    public string ReadBodyText() => bodyText ??= Encoding.UTF8.GetString(Body);

    /// <summary>
    /// Reads a request body into a <see cref="WebhookRequest"/>, refusing anything larger than
    /// <paramref name="maxBytes"/> with an <see cref="EndpointBindingException"/> carrying 413. The
    /// declared <c>Content-Length</c> is checked first, so an oversized body is refused before a byte
    /// of it is read; the running total is checked too, because a chunked body declares nothing.
    /// </summary>
    public static async ValueTask<WebhookRequest> ReadAsync(
        IEnumerable<KeyValuePair<string, StringValues>> headers,
        Stream body,
        long? contentLength,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (contentLength > maxBytes)
            throw new EndpointBindingException(StatusCodes.Status413PayloadTooLarge, "The webhook body is larger than this source accepts.");

        using var buffer = new MemoryStream(contentLength is > 0 ? (int)Math.Min(contentLength.Value, int.MaxValue) : 0);
        var chunk = new byte[ChunkSize];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new EndpointBindingException(StatusCodes.Status413PayloadTooLarge, "The webhook body is larger than this source accepts.");
            buffer.Write(chunk, 0, read);
        }

        return new WebhookRequest(headers, buffer.ToArray());
    }
}
