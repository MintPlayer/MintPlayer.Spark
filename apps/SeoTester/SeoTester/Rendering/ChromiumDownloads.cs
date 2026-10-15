using System.Buffers;
using System.Text;

namespace SeoTester.Rendering;

/// <summary>
/// Whether Chromium would save a navigation's response as a download instead of rendering it
/// (<c>docs/seo_analyzer_PRD.md</c> §12, "Downloads never reach Chromium"). <see cref="BrowserRouter"/> aborts such a
/// navigation, because Chromium writes what it is about to save to a temp file of its own before the download is refused
/// (owner decision 2026-10-15). Mirrors headless Chromium as measured with Playwright 1.63's build: one iframe per response,
/// its download event the verdict, every measured response kept as a case in <c>ChromiumDownloadsTests</c>.
/// </summary>
internal static class ChromiumDownloads
{
    /// <summary>How much of a body Chromium looks at for binary bytes.</summary>
    private const int SniffBytes = 1024;

    /// <summary>The text types Chromium saves instead of showing: calendars, contacts, spreadsheets, RTF, query files.</summary>
    private static readonly HashSet<string> SavedTextTypes = new(StringComparer.Ordinal)
    {
        "text/calendar", "text/x-calendar", "text/x-vcalendar", "text/vcalendar", "text/vcard", "text/x-vcard", "text/x-vcf",
        "text/directory", "text/ldif", "text/qif", "text/x-qif", "text/csv", "text/x-csv", "text/comma-separated-values",
        "text/tab-separated-values", "text/tsv", "text/rtf", "text/ofx", "text/vnd.sun.j2me.app-descriptor", "text/x-ms-iqy",
        "text/x-ms-odc", "text/x-ms-rqy", "text/x-ms-contact",
    };

    /// <summary>
    /// The other types Chromium renders: XML and its feeds, JSON (and <c>application/*+json</c>), script, the images and
    /// media it decodes, HLS playlists. Not every <c>+xml</c>, image or media type: the rest is saved. The media list is
    /// the build's codecs; a build without one saves that type, which then passes as before.
    /// </summary>
    private static readonly HashSet<string> RenderedTypes = new(StringComparer.Ordinal)
    {
        "application/xml", "application/xhtml+xml", "application/rss+xml", "application/atom+xml", "image/svg+xml",
        "application/json", "application/javascript", "application/x-javascript", "application/ecmascript", "application/x-ecmascript",
        "image/png", "image/x-png", "image/apng", "image/jpeg", "image/pjpeg", "image/jpg", "image/gif", "image/webp", "image/avif",
        "image/bmp", "image/x-icon", "image/vnd.microsoft.icon", "image/x-xbitmap",
        "audio/mpeg", "audio/mp3", "audio/x-mp3", "audio/mp4", "audio/x-m4a", "audio/aac", "audio/ogg", "audio/wav", "audio/x-wav",
        "audio/webm", "audio/flac", "video/mp4", "video/x-m4v", "video/webm", "video/ogg", "video/x-matroska",
        "application/ogg", "application/x-mpegurl", "application/vnd.apple.mpegurl",
    };

    /// <summary>The starts that make an unlabelled body HTML, after leading whitespace, in any case (Chromium's list).</summary>
    private static readonly byte[][] HtmlStarts =
    [
        .. new[] { "<!doctype html", "<script", "<html", "<!--", "<head", "<iframe", "<h1", "<div", "<font", "<table", "<a", "<style",
            "<title", "<b", "<body", "<br", "<p" }.Select(Encoding.ASCII.GetBytes),
    ];

    /// <summary>Signatures of what Chromium saves that need not hold a binary byte: PDF, PostScript, RealMedia, TIFF, WMF, EXE.</summary>
    private static readonly byte[][] SavedSignatures =
        ["%PDF-"u8.ToArray(), "%!PS-Adobe-"u8.ToArray(), ".RMF"u8.ToArray(), "I I"u8.ToArray(), "II*"u8.ToArray(), [0xD7, 0xCD, 0xC6, 0x9A], "MZ"u8.ToArray()];

    /// <summary>Signatures of what Chromium renders although binary bytes follow: images, WebM, MP3, and what it takes for text.</summary>
    private static readonly byte[][] RenderedSignatures =
    [
        "GIF87a"u8.ToArray(), "GIF89a"u8.ToArray(), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], [0xFF, 0xD8, 0xFF], "BM"u8.ToArray(),
        "ID3"u8.ToArray(), [0x1A, 0x45, 0xDF, 0xA3], "#!"u8.ToArray(), "%!"u8.ToArray(), "From"u8.ToArray(), ">From"u8.ToArray(),
    ];

    /// <summary>The binary data bytes (WHATWG MIME Sniffing): control characters other than tab, line feed, form feed, carriage return and escape.</summary>
    private static readonly SearchValues<byte> BinaryBytes =
        SearchValues.Create(Enumerable.Range(0, 0x20).Where(b => b is <= 0x08 or 0x0B or (>= 0x0E and <= 0x1A) or >= 0x1C).Select(b => (byte)b).ToArray());

    private static readonly SearchValues<byte> Whitespace = SearchValues.Create([0x09, 0x0A, 0x0C, 0x0D, 0x20]);

    private static readonly SearchValues<char> TokenChars =
        SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    public static bool WouldDownload(RoutedResponse answer) => WouldDownload(answer.Status, answer.Header("content-type"),
        answer.Header("content-disposition"), answer.Header("x-content-type-options"), answer.Body);

    /// <param name="contentTypeOptions">The <c>X-Content-Type-Options</c> header: <c>nosniff</c> turns sniffing off.</param>
    public static bool WouldDownload(int status, string? contentType, string? contentDisposition, string? contentTypeOptions, ReadOnlySpan<byte> body)
    {
        // A redirect is followed (its target is asked again) and an error status shows an error page: any 2xx can be saved, 204 too.
        if (status is < 200 or > 299)
            return false;
        if (IsAttachment(contentDisposition))
            return true;
        var noSniff = contentTypeOptions is not null && contentTypeOptions.Split(',')[0].Trim().Equals("nosniff", StringComparison.OrdinalIgnoreCase);
        var type = Essence(contentType);
        return type switch
        {
            // No usable type: sniffed, or shown as text under nosniff.
            null => !noSniff && SniffedAsSaved(body),
            "unknown/unknown" or "application/unknown" => noSniff || SniffedAsSaved(body),
            // Plain text is sniffed for binary content only.
            "text/plain" => !noSniff && LooksBinary(body),
            _ when type.StartsWith("text/", StringComparison.Ordinal) => SavedTextTypes.Contains(type),
            _ => !RenderedTypes.Contains(type) && !(type.StartsWith("application/", StringComparison.Ordinal) && type.EndsWith("+json", StringComparison.Ordinal)),
        };
    }

    /// <summary>
    /// The disposition type is what precedes the first <c>;</c> or <c>,</c>, without the spaces and tabs around it. Any token
    /// but <c>inline</c> is saved, <c>attachment</c> and the unknown ones alike (RFC 6266 §4.2); an empty type or one that is
    /// not a token (a parameter, a quoted string) leaves the response to its media type.
    /// </summary>
    private static bool IsAttachment(string? disposition)
    {
        if (string.IsNullOrEmpty(disposition))
            return false;
        var span = disposition.AsSpan();
        var end = span.IndexOfAny(';', ',');
        var type = (end < 0 ? span : span[..end]).Trim(" \t");
        return IsToken(type) && !type.Equals("inline", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The media type without parameters, in lower case; null when there is none to use: missing, empty, malformed or <c>*/*</c>.</summary>
    private static string? Essence(string? contentType)
    {
        if (contentType is null)
            return null;
        var end = contentType.IndexOf(';');
        var essence = (end < 0 ? contentType : contentType[..end]).Trim().ToLowerInvariant();
        var slash = essence.IndexOf('/');
        return slash < 0 || essence == "*/*" || !IsToken(essence.AsSpan(0, slash)) || !IsToken(essence.AsSpan(slash + 1)) ? null : essence;
    }

    private static bool IsToken(ReadOnlySpan<char> value) => !value.IsEmpty && !value.ContainsAnyExcept(TokenChars);

    /// <summary>A body with no usable type, sniffed in Chromium's order: byte order mark, markup, signatures, binary bytes.</summary>
    private static bool SniffedAsSaved(ReadOnlySpan<byte> body)
    {
        if (HasByteOrderMark(body) || LooksLikeMarkup(body))
            return false;
        foreach (var signature in SavedSignatures)
            if (body.StartsWith(signature))
                return true;
        return LooksBinary(body);
    }

    /// <summary>A binary byte in the first <see cref="SniffBytes"/>, unless a byte order mark or a rendered signature comes first.</summary>
    private static bool LooksBinary(ReadOnlySpan<byte> body)
    {
        if (HasByteOrderMark(body) || IsWebP(body))
            return false;
        foreach (var signature in RenderedSignatures)
            if (body.StartsWith(signature))
                return false;
        return body[..Math.Min(body.Length, SniffBytes)].IndexOfAny(BinaryBytes) >= 0;
    }

    /// <summary>Chromium's HTML and XML sniffing: after leading whitespace, one of its tags (any case) or <c>&lt;?xml</c> (exactly).</summary>
    private static bool LooksLikeMarkup(ReadOnlySpan<byte> body)
    {
        var start = body.IndexOfAnyExcept(Whitespace);
        if (start < 0)
            return false;
        var rest = body[start..];
        if (rest.StartsWith("<?xml"u8))
            return true;
        foreach (var tag in HtmlStarts)
            if (rest.Length >= tag.Length && Ascii.EqualsIgnoreCase(rest[..tag.Length], tag))
                return true;
        return false;
    }

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];
    private static ReadOnlySpan<byte> Utf16BigEndianBom => [0xFE, 0xFF];
    private static ReadOnlySpan<byte> Utf16LittleEndianBom => [0xFF, 0xFE];

    private static bool HasByteOrderMark(ReadOnlySpan<byte> body)
        => body.StartsWith(Utf8Bom) || body.StartsWith(Utf16BigEndianBom) || body.StartsWith(Utf16LittleEndianBom);

    private static bool IsWebP(ReadOnlySpan<byte> body) => body.Length >= 14 && body.StartsWith("RIFF"u8) && body[8..14].SequenceEqual("WEBPVP"u8);
}
