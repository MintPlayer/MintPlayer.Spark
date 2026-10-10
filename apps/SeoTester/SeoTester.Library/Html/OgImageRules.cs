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
    /// The media type the bytes are, by their signature: PNG, JPEG, GIF or WebP. Null for anything else, SVG
    /// included (an SVG is a document that can carry script), whatever the response's <c>Content-Type</c> said.
    /// </summary>
    public static string? RasterType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return "image/png";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
            return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
            return "image/gif";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }
}
