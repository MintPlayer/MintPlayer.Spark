using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace MintPlayer.Spark.PageMeta;

/// <summary>
/// Rewrites the SPA's <c>index.html</c> for one page (X6): the template's own title, description, OpenGraph and Twitter
/// tags and canonical link are removed, and the page's are inserted right after <c>&lt;head&gt;</c>. Pure, so the
/// escaping is a unit test away.
/// </summary>
internal static partial class SparkPageMetaHtml
{
    /// <summary>
    /// HTML-encodes <c>&lt; &gt; &amp; " ' +</c> and the like but keeps letters of every script readable. Every value
    /// goes through it, text and attribute alike: the title and description are seller text.
    /// </summary>
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    /// <summary>The marker on every inserted tag, so the SPA (and a test) can tell server tags from its own.</summary>
    internal const string Marker = "data-spark-meta";

    /// <summary>
    /// <paramref name="html"/> with the tags of <paramref name="meta"/>, or <see langword="null"/> when it has no
    /// <c>&lt;head&gt;</c> (then it is served untouched). <paramref name="canonical"/> and <paramref name="image"/> are
    /// absolute or <see langword="null"/>; a null one is left out.
    /// </summary>
    internal static string? Rewrite(string html, SparkPageMeta meta, Uri? canonical, Uri? image,
        IReadOnlyList<(string Language, Uri Href)>? alternates = null)
    {
        var head = HeadOpen().Match(html);
        if (!head.Success)
            return null;

        var before = html[..(head.Index + head.Length)];
        var after = html[(head.Index + head.Length)..];

        // Only the head's own tags: a <title> inside an inline <svg> in the body is not the page title.
        var headClose = HeadClose().Match(after);
        var headInner = headClose.Success ? after[..headClose.Index] : after;
        var rest = headClose.Success ? after[headClose.Index..] : string.Empty;

        headInner = Title().Replace(headInner, string.Empty, 1);
        headInner = ReplacedMeta().Replace(headInner, string.Empty);
        headInner = CanonicalLink().Replace(headInner, string.Empty);
        if (alternates is { Count: > 0 })
            headInner = HreflangLink().Replace(headInner, string.Empty);
        if (meta.StructuredData is { Count: > 0 })
            headInner = JsonLdScript().Replace(headInner, string.Empty);

        var tags = new StringBuilder();
        tags.Append('\n');
        tags.Append("<title ").Append(Marker).Append('>').Append(Encoder.Encode(meta.Title)).Append("</title>\n");
        Meta(tags, "name", "description", meta.Description);
        Meta(tags, "property", "og:title", meta.Title);
        Meta(tags, "property", "og:description", meta.Description);
        Meta(tags, "property", "og:type", meta.Type);
        if (image is not null)
            Meta(tags, "property", "og:image", image.AbsoluteUri);
        if (canonical is not null)
            Meta(tags, "property", "og:url", canonical.AbsoluteUri);
        Meta(tags, "name", "twitter:card", meta.TwitterCard);
        if (canonical is not null)
            tags.Append("<link rel=\"canonical\" href=\"").Append(Encoder.Encode(canonical.AbsoluteUri)).Append("\" ").Append(Marker).Append(">\n");
        foreach (var (language, href) in alternates ?? [])
            tags.Append("<link rel=\"alternate\" hreflang=\"").Append(Encoder.Encode(language)).Append("\" href=\"")
                .Append(Encoder.Encode(href.AbsoluteUri)).Append("\" ").Append(Marker).Append(">\n");
        foreach (var data in meta.StructuredData ?? [])
            tags.Append("<script type=\"application/ld+json\" ").Append(Marker).Append('>')
                .Append(JsonLd(data)).Append("</script>\n");

        return before + tags + headInner + rest;
    }

    /// <summary>
    /// The JSON-LD of <paramref name="data"/>, safe inside a <c>&lt;script&gt;</c> element: the default JavaScript encoder
    /// escapes <c>&lt; &gt; &amp; ' "</c> as <c><</c> etc., so seller text such as <c>&lt;/script&gt;</c> cannot close
    /// the element. Letters of every script stay readable.
    /// </summary>
    internal static string JsonLd(SparkStructuredData data)
        => data.ToJsonLd().ToJsonString(JsonLdOptions);

    private static readonly System.Text.Json.JsonSerializerOptions JsonLdOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private static void Meta(StringBuilder tags, string kind, string key, string value)
        => tags.Append("<meta ").Append(kind).Append("=\"").Append(key).Append("\" content=\"").Append(Encoder.Encode(value))
               .Append("\" ").Append(Marker).Append(">\n");

    [GeneratedRegex(@"<head(\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadOpen();

    [GeneratedRegex(@"</head\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadClose();

    [GeneratedRegex(@"<title(\s[^>]*)?>.*?</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Title();

    /// <summary>The template's description, OpenGraph and Twitter tags: a second copy would leave a crawler to pick one.</summary>
    [GeneratedRegex(@"<meta\b[^>]*\b(name|property)\s*=\s*[""']?(description|og:[\w:]+|twitter:[\w:]+)[""']?(?=[\s/>])[^>]*>\s*",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReplacedMeta();

    [GeneratedRegex(@"<link\b[^>]*\brel\s*=\s*[""']?canonical[""']?(?=[\s/>])[^>]*>\s*", RegexOptions.IgnoreCase)]
    private static partial Regex CanonicalLink();

    [GeneratedRegex(@"<link\b(?=[^>]*\bhreflang\s*=)[^>]*>\s*", RegexOptions.IgnoreCase)]
    private static partial Regex HreflangLink();

    [GeneratedRegex(@"<script\b[^>]*\btype\s*=\s*[""']?application/ld\+json[""']?[^>]*>.*?</script\s*>\s*", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex JsonLdScript();
}
