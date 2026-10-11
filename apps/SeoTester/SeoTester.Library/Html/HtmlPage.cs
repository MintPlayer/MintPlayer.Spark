using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace SeoTester.Html;

/// <summary>
/// A page's raw HTML parsed as a browser would (AngleSharp, HTML5 tree construction with error recovery), with no
/// script execution and a cap on what is parsed (spike S9). Everything in it is untrusted.
/// </summary>
public sealed partial class HtmlPage
{
    /// <summary>Characters of HTML parsed; the rest is ignored (2 MiB covers every real page; a bigger one is suspect).</summary>
    public const int MaxCharacters = 2 * 1024 * 1024;

    private HtmlPage(IHtmlDocument document, int parseErrors, bool truncated)
    {
        Document = document;
        ParseErrors = parseErrors;
        Truncated = truncated;
    }

    public IHtmlDocument Document { get; }

    /// <summary>Parse errors the tree builder recovered from (a light stand-in for a validator, research §1.7).</summary>
    public int ParseErrors { get; }

    /// <summary>The HTML was longer than <see cref="MaxCharacters"/> and was cut.</summary>
    public bool Truncated { get; }

    public static HtmlPage Parse(string html)
    {
        var truncated = html.Length > MaxCharacters;
        if (truncated)
            html = html[..MaxCharacters];
        var errors = 0;
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false });
        parser.Error += (_, _) => errors++;
        var document = parser.ParseDocument(html);
        return new HtmlPage(document, errors, truncated);
    }

    // ---- What many checks read. ---------------------------------------------------------------------------------

    public IReadOnlyList<IElement> Titles => Document.Head?.QuerySelectorAll("title").ToList() ?? (IReadOnlyList<IElement>)[];

    public string? Title => Titles.FirstOrDefault()?.TextContent is { } t ? Collapse(t) : null;

    public string? Meta(string name)
        => Document.QuerySelectorAll("meta[name]").FirstOrDefault(m => string.Equals(m.GetAttribute("name"), name, StringComparison.OrdinalIgnoreCase))?.GetAttribute("content");

    public string? MetaProperty(string property)
        => Document.QuerySelectorAll("meta[property], meta[name]").FirstOrDefault(m =>
            string.Equals(m.GetAttribute("property"), property, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.GetAttribute("name"), property, StringComparison.OrdinalIgnoreCase))?.GetAttribute("content");

    public string? Description => Meta("description") is { } d ? Collapse(d) : null;

    public IReadOnlyList<string> H1s => Document.QuerySelectorAll("h1").Select(h => Collapse(h.TextContent)).ToList();

    /// <summary>Every <c>link</c> whose <c>rel</c> holds <paramref name="rel"/> (rel is a token list).</summary>
    public IEnumerable<IElement> Links(string rel)
        => Document.QuerySelectorAll("link[rel]").Where(l => HasToken(l.GetAttribute("rel"), rel));

    public static bool HasToken(string? list, string token)
        => list is not null && list.Split((char[])[' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries).Any(t => string.Equals(t, token, StringComparison.OrdinalIgnoreCase));

    /// <summary>The <c>content</c> of <c>&lt;meta http-equiv=…&gt;</c>.</summary>
    public string? HttpEquiv(string name)
        => Document.QuerySelectorAll("meta[http-equiv]").FirstOrDefault(m => string.Equals(m.GetAttribute("http-equiv"), name, StringComparison.OrdinalIgnoreCase))?.GetAttribute("content");

    /// <summary>The words of the main content: the body without scripts, styles, navigation, header, footer and asides.</summary>
    public int MainWordCount
    {
        get
        {
            if (Document.Body is not { } body)
                return 0;
            var clone = (IElement)body.Clone(true);
            foreach (var element in clone.QuerySelectorAll("script, style, noscript, template, nav, header, footer, aside, svg").ToList())
                element.Remove();
            return Words().Matches(clone.TextContent).Count;
        }
    }

    /// <summary>The visible text of the body, whitespace collapsed (for the duplicate-content fingerprint).</summary>
    public string BodyText
    {
        get
        {
            if (Document.Body is not { } body)
                return string.Empty;
            var clone = (IElement)body.Clone(true);
            foreach (var element in clone.QuerySelectorAll("script, style, noscript, template, svg").ToList())
                element.Remove();
            return Collapse(clone.TextContent);
        }
    }

    public static string Collapse(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’-]*", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Words();
}
