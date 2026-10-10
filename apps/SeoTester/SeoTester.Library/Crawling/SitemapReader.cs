using System.Xml;

namespace SeoTester.Crawling;

/// <summary>What one sitemap file held.</summary>
/// <param name="IsIndex">A <c>sitemapindex</c> (its locations are sitemaps), else a <c>urlset</c>.</param>
/// <param name="Locations">The first <c>loc</c> values, up to the reader's limit.</param>
/// <param name="Total">Every <c>loc</c> counted, also past the limit.</param>
/// <param name="HasStylesheet">An <c>&lt;?xml-stylesheet?&gt;</c> instruction (cosmetic).</param>
/// <param name="Error">Why the file is not a valid sitemap; null when it is.</param>
public sealed record SitemapContent(bool IsIndex, IReadOnlyList<string> Locations, int Total, bool HasStylesheet, string? Error)
{
    /// <summary>The sitemaps protocol's limit on URLs per file.</summary>
    public const int ProtocolMaxUrls = 50_000;
}

/// <summary>
/// Reads a sitemap (sitemaps.org) as a stream, never as a whole document: no DTD, no external entities, a cap on the
/// characters read, and at most <c>keep</c> locations kept. Plain-text sitemaps (one URL per line) are read too.
/// </summary>
public static class SitemapReader
{
    /// <summary>Most characters read from one sitemap (the protocol's 50 MB uncompressed).</summary>
    public const long MaxCharacters = 50L * 1024 * 1024;

    public static SitemapContent Read(Stream stream, int keep)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCharacters,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            Async = false,
        };
        var locations = new List<string>();
        int total = 0;
        bool? isIndex = null;
        var stylesheet = false;
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.ProcessingInstruction && reader.Name == "xml-stylesheet")
                    stylesheet = true;
                else if (reader.NodeType == XmlNodeType.Element)
                {
                    if (isIndex is null)
                    {
                        isIndex = reader.LocalName switch
                        {
                            "sitemapindex" => true,
                            "urlset" => false,
                            _ => throw new XmlException($"The root element is <{reader.LocalName}>, not <urlset> or <sitemapindex>."),
                        };
                    }
                    else if (reader.LocalName == "loc")
                    {
                        // Moves past the end tag itself, so the loop must not Read() again.
                        var value = reader.ReadElementContentAsString().Trim();
                        total++;
                        if (locations.Count < keep && value.Length is > 0 and <= CrawlUrl.MaxLength)
                            locations.Add(value);
                        continue;
                    }
                }
                reader.Read();
            }
        }
        catch (XmlException ex)
        {
            return new SitemapContent(isIndex ?? false, locations, total, stylesheet, ex.Message.Length > 200 ? ex.Message[..200] : ex.Message);
        }
        return isIndex is null
            ? new SitemapContent(false, [], 0, stylesheet, "The file is empty.")
            : new SitemapContent(isIndex.Value, locations, total, stylesheet, null);
    }

    /// <summary>A text sitemap: one absolute URL per line.</summary>
    public static SitemapContent ReadText(string text, int keep)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var urls = lines.Where(l => Uri.TryCreate(l, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)).ToList();
        return new SitemapContent(false, urls.Take(keep).ToList(), urls.Count, false, urls.Count == lines.Length ? null : "Some lines are not absolute URLs.");
    }
}
