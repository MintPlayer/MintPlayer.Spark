using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SeoTester.Crawling;
using SeoTester.Html;

namespace SeoTester.Checks;

/// <summary>
/// Readers for what the probe downloads besides pages: stylesheets, scripts, the web app manifest, the RDAP bootstrap
/// and answers, and hstspreload.org's status. All input is untrusted: size caps, regexes with timeouts, no throws.
/// </summary>
public static partial class SiteParsers
{
    // ---- CSS (performance.font-display, accessibility.dark-mode / reduced-motion) ----------------------------------

    /// <summary>Scans stylesheets and <c>&lt;style&gt;</c> blocks; Google Fonts links count as font faces that need <c>display=</c>.</summary>
    public static CssFacts ScanCss(IEnumerable<string> styles, IEnumerable<string> googleFontUrls, bool colorSchemeMeta)
    {
        int faces = 0, withoutDisplay = 0;
        bool dark = colorSchemeMeta, motion = false;
        foreach (var css in styles)
        {
            var text = css.Length > 2 * 1024 * 1024 ? css[..(2 * 1024 * 1024)] : css;
            foreach (Match face in FontFace().Matches(text))
            {
                faces++;
                if (!face.Value.Contains("font-display", StringComparison.OrdinalIgnoreCase))
                    withoutDisplay++;
            }
            dark |= text.Contains("prefers-color-scheme", StringComparison.OrdinalIgnoreCase) || ColorScheme().IsMatch(text);
            motion |= text.Contains("prefers-reduced-motion", StringComparison.OrdinalIgnoreCase);
        }
        foreach (var url in googleFontUrls)
        {
            faces++;
            if (!url.Contains("display=", StringComparison.OrdinalIgnoreCase))
                withoutDisplay++;
        }
        return new CssFacts(faces, withoutDisplay, dark, motion);
    }

    [GeneratedRegex(@"@font-face\s*\{[^}]{0,4000}\}", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FontFace();

    [GeneratedRegex(@"color-scheme\s*:\s*[^;]*dark", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ColorScheme();

    /// <summary>
    /// Whether CSS or JavaScript is minified, by the share of whitespace and comments: above 10 % of the file
    /// (research §1.3) it is not. Files under 2 KB are not judged. Returns the estimated saving in bytes.
    /// </summary>
    public static (bool? Minified, long Saving) JudgeMinified(string content)
    {
        if (content.Length < 2048)
            return (null, 0);
        var sample = content.Length > 512 * 1024 ? content[..(512 * 1024)] : content;
        long removable = 0;
        foreach (Match comment in BlockComment().Matches(sample))
            removable += comment.Length;
        var lines = sample.Split('\n');
        foreach (var line in lines)
        {
            var leading = line.Length - line.TrimStart().Length;
            removable += leading;
        }
        removable += lines.Length - 1;
        var ratio = removable / (double)sample.Length;
        var saving = (long)(ratio * content.Length);
        return (ratio <= 0.10, saving);
    }

    [GeneratedRegex(@"/\*.{0,10000}?\*/", RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BlockComment();

    // ---- Web app manifest (mobile.manifest) ---------------------------------------------------------------------------

    public static IReadOnlyList<string> ManifestProblems(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ["empty"];
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ["not a JSON object"];
            var problems = new List<string>();
            if (!Has(root, "name") && !Has(root, "short_name"))
                problems.Add("no name");
            if (!Has(root, "start_url"))
                problems.Add("no start_url");
            if (!(root.TryGetProperty("display", out var display) && display.ValueKind == JsonValueKind.String
                  && display.GetString() is "standalone" or "fullscreen" or "minimal-ui"))
                problems.Add("display is not standalone, fullscreen or minimal-ui");
            var sizes = root.TryGetProperty("icons", out var icons) && icons.ValueKind == JsonValueKind.Array
                ? icons.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object && i.TryGetProperty("sizes", out _))
                    .SelectMany(i => (i.GetProperty("sizes").GetString() ?? string.Empty).Split(' ')).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            if (!sizes.Contains("192x192"))
                problems.Add("no 192×192 icon");
            if (!sizes.Contains("512x512"))
                problems.Add("no 512×512 icon");
            if (!Has(root, "theme_color"))
                problems.Add("no theme_color");
            return problems;
        }
        catch (JsonException)
        {
            return ["invalid JSON"];
        }

        static bool Has(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Length > 0;
    }

    // ---- RDAP (domain.expiry, spike S4) -----------------------------------------------------------------------------

    /// <summary>The RDAP base URL for <paramref name="domain"/>'s TLD from IANA's bootstrap (<c>dns.json</c>), or null.</summary>
    public static string? RdapBaseFor(string bootstrapJson, string domain)
    {
        var tld = domain.TrimEnd('.').Split('.')[^1].ToLowerInvariant();
        try
        {
            using var document = JsonDocument.Parse(bootstrapJson, new JsonDocumentOptions { MaxDepth = 8 });
            foreach (var service in document.RootElement.GetProperty("services").EnumerateArray())
            {
                var entry = service.EnumerateArray().ToList();
                if (entry.Count < 2)
                    continue;
                if (entry[0].EnumerateArray().Any(t => string.Equals(t.GetString(), tld, StringComparison.OrdinalIgnoreCase)))
                    return entry[1].EnumerateArray().Select(u => u.GetString()).FirstOrDefault(u => u is not null && u.StartsWith("https://", StringComparison.Ordinal));
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }
        return null;
    }

    /// <summary>The <c>expiration</c> event of an RDAP domain answer, or null when the registry publishes none (.nl).</summary>
    public static DateTime? RdapExpiry(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (!document.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var e in events.EnumerateArray())
                if (e.TryGetProperty("eventAction", out var action) && action.GetString() == "expiration"
                    && e.TryGetProperty("eventDate", out var date)
                    && DateTimeOffset.TryParse(date.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
                    return when.UtcDateTime;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
        }
        return null;
    }

    // ---- hstspreload.org (security.hsts-preload, spike S10) ---------------------------------------------------------

    /// <summary>The <c>status</c> of an <c>api/v2/status</c> answer (<c>unknown</c>, <c>pending</c>, <c>preloaded</c>, <c>rejected</c>), or null.</summary>
    public static string? HstsPreloadStatus(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String ? status.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- Links and assets of a page --------------------------------------------------------------------------------

    /// <summary>The page's links: internal (same host, any scheme) to crawl and external ones (for the broken-external check).</summary>
    public static (IReadOnlyList<Uri> Internal, IReadOnlyList<Uri> External) Links(HtmlPage html, Uri pageUrl, string host, int max = 500)
    {
        var baseUri = html.Document.QuerySelector("base[href]")?.GetAttribute("href") is { } b && Uri.TryCreate(pageUrl, b, out var bu) ? bu : pageUrl;
        var internalLinks = new List<Uri>();
        var external = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var anchor in html.Document.QuerySelectorAll("a[href], area[href]"))
        {
            if (HtmlPage.HasToken(anchor.GetAttribute("rel"), "nofollow") || anchor.HasAttribute("download"))
                continue;
            if (CrawlUrl.Resolve(baseUri, anchor.GetAttribute("href")) is not { } uri || !seen.Add(uri.AbsoluteUri))
                continue;
            (CrawlUrl.IsSameHost(uri, host) ? internalLinks : external).Add(uri);
            if (seen.Count >= max)
                break;
        }
        return (internalLinks, external);
    }

    /// <summary>Same-host stylesheets and scripts of a page, for the asset checks (at most <paramref name="max"/> of each).</summary>
    public static (IReadOnlyList<Uri> Styles, IReadOnlyList<Uri> Scripts) Assets(HtmlPage html, Uri pageUrl, int max)
    {
        var styles = html.Links("stylesheet").Select(l => CrawlUrl.Resolve(pageUrl, l.GetAttribute("href")))
            .Where(u => u is not null && CrawlUrl.IsSameHost(u, pageUrl.IdnHost)).Select(u => u!).DistinctBy(u => u.AbsoluteUri).Take(max).ToList();
        var scripts = html.Document.QuerySelectorAll("script[src]").Select(s => CrawlUrl.Resolve(pageUrl, s.GetAttribute("src")))
            .Where(u => u is not null && CrawlUrl.IsSameHost(u, pageUrl.IdnHost)).Select(u => u!).DistinctBy(u => u.AbsoluteUri).Take(max).ToList();
        return (styles, scripts);
    }

    /// <summary>Folders of a page's same-host assets (<c>/images/</c>, <c>/css/</c>), the only ones the directory-listing probe asks for.</summary>
    public static IReadOnlyList<string> AssetDirectories(HtmlPage html, Uri pageUrl, int max)
    {
        return html.Document.QuerySelectorAll("img[src], script[src], link[href]")
            .Select(e => CrawlUrl.Resolve(pageUrl, e.GetAttribute(e.LocalName == "link" ? "href" : "src")))
            .Where(u => u is not null && CrawlUrl.IsSameHost(u, pageUrl.IdnHost))
            .Select(u => u!.AbsolutePath)
            .Select(p => p[..(p.LastIndexOf('/') + 1)])
            .Where(d => d.Length > 1)
            .Distinct(StringComparer.Ordinal)
            .Take(max)
            .ToList();
    }
}
