namespace SeoTester.Html;

/// <summary>
/// What the shared-link card may show of a page's <c>og:image</c> (<c>docs/seo_analyzer_PRD.md</c> Q-M11d): the server
/// fetches it over the SSRF-safe client and hands the viewer the bytes only when they are a raster image of a known
/// kind, so the viewer's browser never asks the audited site (or wherever the tag points) for anything.
/// </summary>
public static class OgImageRules
{
    /// <summary>Most bytes fetched; a larger image is refused, not cut (a cut image is not an image).</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    /// <summary>
    /// The tag's value as a fetchable address: absolute <c>http</c>/<c>https</c> only (the Open Graph protocol
    /// requires an absolute URL, and the check flags any other), no user info. Null for anything else.
    /// </summary>
    public static Uri? FetchableUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;
        return string.IsNullOrEmpty(uri.UserInfo) ? uri : null;
    }

    /// <summary>
    /// The media type the bytes are: PNG, JPEG, GIF or WebP, by their signature, and only when the file is well formed
    /// to its end and carries no markup. Null for anything else, whatever the response's <c>Content-Type</c> said:
    /// SVG (a document that can carry script), and a polyglot, an image signature with an HTML page behind it or in a
    /// text chunk (<c>docs/seo_analyzer_PRD.md</c> §12, "Untrusted content: proof tests"):
    /// <list type="bullet">
    /// <item>PNG: a chain of chunks that fits exactly, IHDR first, an IDAT, IEND last with nothing after it;</item>
    /// <item>JPEG: start of image, and end of image (<c>FF D9</c>) as the last two bytes;</item>
    /// <item>GIF: the trailer (<c>3B</c>) as the last byte;</item>
    /// <item>WebP: the RIFF size equal to the file's, chunks that fit exactly, a VP8, VP8L or VP8X chunk first;</item>
    /// <item>and nowhere a tag that starts a document or a script (<c>&lt;script</c>, <c>&lt;html</c>, <c>&lt;svg</c>, …),
    /// matched without regard to case (a 7-byte match in compressed image data is about one in 2<sup>56</sup> per offset).</item>
    /// </list>
    /// </summary>
    public static string? RasterType(ReadOnlySpan<byte> bytes)
    {
        var type = Signature(bytes);
        if (type is null || HoldsMarkup(bytes))
            return null;
        var wellFormed = type switch
        {
            "image/png" => PngIsWellFormed(bytes),
            "image/jpeg" => bytes.Length >= 4 && bytes[^2] == 0xFF && bytes[^1] == 0xD9,
            "image/gif" => bytes.Length >= 14 && bytes[^1] == 0x3B,
            _ => WebpIsWellFormed(bytes),
        };
        return wellFormed ? type : null;
    }

    private static string? Signature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(PngSignature))
            return "image/png";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
            return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
            return "image/gif";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static bool PngIsWellFormed(ReadOnlySpan<byte> bytes)
    {
        var position = PngSignature.Length;
        var first = true;
        var data = false;
        while (bytes.Length - position >= 12)
        {
            var length = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes[position..]);
            var chunk = bytes.Slice(position + 4, 4);
            foreach (var c in chunk)
                if (!char.IsAsciiLetter((char)c))
                    return false;
            if (length > bytes.Length - position - 12)
                return false;
            if (first && !(chunk.SequenceEqual("IHDR"u8) && length == 13))
                return false;
            first = false;
            data |= chunk.SequenceEqual("IDAT"u8);
            position += 12 + (int)length;
            if (chunk.SequenceEqual("IEND"u8))
                return length == 0 && position == bytes.Length && data;
        }
        return false;
    }

    private static bool WebpIsWellFormed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != bytes.Length - 8)
            return false;
        var first = bytes.Slice(12, 4);
        if (!(first.SequenceEqual("VP8 "u8) || first.SequenceEqual("VP8L"u8) || first.SequenceEqual("VP8X"u8)))
            return false;
        long position = 12;
        while (position < bytes.Length)
        {
            if (bytes.Length - position < 8)
                return false;
            var size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)(position + 4)..]);
            position += 8 + size + (size & 1);
        }
        return position == bytes.Length;
    }

    /// <summary>Tags that start an HTML or SVG document or run script, lower case.</summary>
    private static readonly byte[][] Markup =
    [
        "<script"u8.ToArray(), "<html"u8.ToArray(), "<svg"u8.ToArray(), "<!doctype"u8.ToArray(), "<head"u8.ToArray(),
        "<body"u8.ToArray(), "<iframe"u8.ToArray(), "<object"u8.ToArray(), "<embed"u8.ToArray(),
    ];

    private static bool HoldsMarkup(ReadOnlySpan<byte> bytes)
    {
        for (var at = bytes.IndexOf((byte)'<'); at >= 0; )
        {
            var rest = bytes[at..];
            foreach (var tag in Markup)
            {
                if (rest.Length < tag.Length)
                    continue;
                var match = true;
                for (var i = 1; i < tag.Length && match; i++)
                    match = (rest[i] | 0x20) == tag[i] || rest[i] == tag[i];
                if (match)
                    return true;
            }
            var next = rest[1..].IndexOf((byte)'<');
            at = next < 0 ? -1 : at + 1 + next;
        }
        return false;
    }
}
