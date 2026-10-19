using System.Globalization;
using System.Text;
using System.Xml;
using MintPlayer.Spark.PageMeta;

namespace MintPlayer.Spark.Sitemap;

/// <summary>
/// Writes sitemap files (commerce PRD X22) per the <a href="https://www.sitemaps.org/protocol.html">sitemap protocol</a>
/// 0.9, with Google's <c>xhtml:link</c> hreflang extension. Pure, so the shape is a unit test away.
/// </summary>
internal static class SparkSitemapXml
{
    internal const string Namespace = "http://www.sitemaps.org/schemas/sitemap/0.9";
    internal const string XhtmlNamespace = "http://www.w3.org/1999/xhtml";

    /// <summary>The protocol's limits: URLs per file and characters per <c>loc</c>.</summary>
    internal const int MaxUrlsPerFile = 50_000;
    internal const int MaxLocationLength = 2047;

    private static readonly XmlWriterSettings Settings = new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = false,
        Async = false,
    };

    /// <summary>The sitemap index listing <paramref name="sitemaps"/> (absolute URLs).</summary>
    internal static byte[] Index(IEnumerable<Uri> sitemaps)
    {
        using var stream = new MemoryStream();
        using (var xml = XmlWriter.Create(stream, Settings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("sitemapindex", Namespace);
            foreach (var sitemap in sitemaps.Take(MaxUrlsPerFile))
            {
                xml.WriteStartElement("sitemap", Namespace);
                xml.WriteElementString("loc", Namespace, sitemap.AbsoluteUri);
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }
        return stream.ToArray();
    }

    /// <summary>
    /// A <c>urlset</c> of <paramref name="entries"/>, resolved against <paramref name="origin"/> (ending in <c>/</c>). An
    /// entry whose location is on another origin or too long is left out, as is an invalid alternate.
    /// </summary>
    internal static byte[] UrlSet(IEnumerable<SparkSitemapEntry> entries, Uri origin)
    {
        using var stream = new MemoryStream();
        using (var xml = XmlWriter.Create(stream, Settings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("urlset", Namespace);
            xml.WriteAttributeString("xmlns", "xhtml", null, XhtmlNamespace);
            var written = 0;
            foreach (var entry in entries)
            {
                if (written == MaxUrlsPerFile)
                    break;
                if (Resolve(entry.Location, origin) is not { } location || !SameOrigin(location, origin) || location.AbsoluteUri.Length > MaxLocationLength)
                    continue;

                xml.WriteStartElement("url", Namespace);
                xml.WriteElementString("loc", Namespace, location.AbsoluteUri);
                if (entry.LastModified is { } lastModified)
                    xml.WriteElementString("lastmod", Namespace, lastModified.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                if (entry.ChangeFrequency is { } frequency)
                    xml.WriteElementString("changefreq", Namespace, frequency.ToString().ToLowerInvariant());
                if (entry.Priority is { } priority)
                    xml.WriteElementString("priority", Namespace, Math.Clamp(priority, 0m, 1m).ToString("0.0#", CultureInfo.InvariantCulture));
                foreach (var alternate in entry.Alternates ?? [])
                {
                    if (!alternate.IsValid || Resolve(alternate.Href, origin) is not { } href)
                        continue;
                    xml.WriteStartElement("xhtml", "link", XhtmlNamespace);
                    xml.WriteAttributeString("rel", "alternate");
                    xml.WriteAttributeString("hreflang", alternate.Language);
                    xml.WriteAttributeString("href", href.AbsoluteUri);
                    xml.WriteEndElement();
                }
                xml.WriteEndElement();
                written++;
            }
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }
        return stream.ToArray();
    }

    /// <summary><paramref name="value"/> as an absolute http(s) URL: a rooted path against <paramref name="origin"/>, or as given.</summary>
    internal static Uri? Resolve(string value, Uri origin)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        // A rooted path is relative explicitly: RelativeOrAbsolute would make it file:///… on Linux.
        if (value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal))
            return Uri.TryCreate(value.TrimStart('/'), UriKind.Relative, out var relative) ? new Uri(origin, relative) : null;
        return Uri.TryCreate(value, UriKind.Absolute, out var absolute) && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            ? absolute
            : null;
    }

    private static bool SameOrigin(Uri url, Uri origin)
        => string.Equals(url.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(url.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
           && url.Port == origin.Port;
}
