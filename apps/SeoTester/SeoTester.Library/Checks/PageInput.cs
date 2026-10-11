using SeoTester.Html;

namespace SeoTester.Checks;

/// <summary>One fetched page as the page-level checks see it: the response and, for HTML, the parsed document.</summary>
public sealed class PageInput
{
    /// <summary>The URL that answered (after redirects).</summary>
    public required Uri Url { get; init; }

    public required int StatusCode { get; init; }

    /// <summary>Response headers, names lower-case, repeated headers joined with <c>", "</c> (except <c>set-cookie</c>: <see cref="SetCookies"/>).</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    public IReadOnlyList<string> SetCookies { get; init; } = [];

    /// <summary>The <c>Content-Encoding</c> on the wire (the client decompresses).</summary>
    public string? WireEncoding { get; init; }

    /// <summary>Bytes of the body after decompression.</summary>
    public long BodyBytes { get; init; }

    /// <summary>The first bytes of the body, for "charset declared within the first 1024 bytes".</summary>
    public string? Prefix { get; init; }

    public HtmlPage? Html { get; init; }

    /// <summary>The URL is listed in a sitemap.</summary>
    public bool InSitemap { get; init; }

    /// <summary>The URL was reached through a link on the site.</summary>
    public bool LinkedInternally { get; init; }

    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var value) ? value : null;

    public bool IsHttps => Url.Scheme == Uri.UriSchemeHttps;

    public string? ContentType => Header("content-type");

    public bool IsHtml => ContentType is { } type && (type.Contains("text/html", StringComparison.OrdinalIgnoreCase) || type.Contains("application/xhtml", StringComparison.OrdinalIgnoreCase));

    /// <summary>A <c>noindex</c> (or <c>none</c>) in the meta robots tags or <c>X-Robots-Tag</c>.</summary>
    public bool NoIndex => RobotsDirectives.Any(d => d is "noindex" or "none");

    public bool NoFollow => RobotsDirectives.Any(d => d is "nofollow" or "none");

    private IEnumerable<string> RobotsDirectives
    {
        get
        {
            var values = new List<string>();
            if (Header("x-robots-tag") is { } header)
                values.Add(header);
            if (Html is not null)
                values.AddRange(Html.Document.QuerySelectorAll("meta[name]")
                    .Where(m => m.GetAttribute("name") is { } n && (n.Equals("robots", StringComparison.OrdinalIgnoreCase) || n.Equals("googlebot", StringComparison.OrdinalIgnoreCase)))
                    .Select(m => m.GetAttribute("content") ?? string.Empty));
            // "X-Robots-Tag: googlebot: noindex" names an agent; every directive after a colon still counts.
            return values.SelectMany(v => v.Split(',', ';', ':')).Select(d => d.Trim().ToLowerInvariant());
        }
    }
}
