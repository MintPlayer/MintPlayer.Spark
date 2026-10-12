using System.Text;
using AngleSharp.Dom;

namespace SeoTester.Html;

/// <summary>
/// Where on a page a finding is (<c>docs/spark_mcp_PRD.md</c> §4.7 gap 1): up to <see cref="MaxSelectors"/> CSS selectors
/// of the offending elements and an excerpt of the first one's HTML of at most <see cref="MaxExcerptLength"/> characters,
/// so an agent (or a person) can find the template that renders it. Both are the page's own markup: untrusted, and fenced
/// as data wherever they are shown to an agent.
/// </summary>
/// <param name="Selectors">The elements' selectors, at most <see cref="MaxSelectors"/>, each at most <see cref="MaxSelectorLength"/> characters.</param>
/// <param name="Excerpt">The first element's outer HTML, whitespace collapsed, cut to <see cref="MaxExcerptLength"/>; null when there is none.</param>
public sealed record ElementEvidence(IReadOnlyList<string> Selectors, string? Excerpt)
{
    public const int MaxSelectors = 5;
    public const int MaxSelectorLength = 200;
    public const int MaxExcerptLength = 300;

    /// <summary>Levels a selector climbs without meeting an id before it stops (the path is then unanchored).</summary>
    private const int MaxDepth = 6;

    /// <summary>The evidence of <paramref name="elements"/> in document order, or <see langword="null"/> when there is none.</summary>
    public static ElementEvidence? Of(IEnumerable<IElement> elements)
    {
        var first = elements.Take(MaxSelectors).ToList();
        if (first.Count == 0)
            return null;
        return new ElementEvidence(
            first.Select(SelectorOf).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
            ExcerptOf(first[0].LocalName is "html" or "head" or "body" ? StartTag(first[0]) : first[0].OuterHtml));
    }

    /// <summary>A document-sized element's start tag only: its outer HTML would be the whole page.</summary>
    private static string StartTag(IElement element)
        => "<" + element.LocalName + string.Concat(element.Attributes.Select(a => $" {a.Name}=\"{a.Value}\"")) + ">";

    /// <summary>
    /// Evidence from selectors and HTML something else already computed (axe-core's node targets), with the same caps.
    /// </summary>
    public static ElementEvidence? Of(IEnumerable<string>? selectors, string? html)
    {
        var list = (selectors ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => Cut(s.Trim(), MaxSelectorLength))
            .Distinct(StringComparer.Ordinal).Take(MaxSelectors).ToList();
        var excerpt = ExcerptOf(html);
        return list.Count == 0 && excerpt is null ? null : new ElementEvidence(list, excerpt);
    }

    /// <summary>
    /// A CSS selector for <paramref name="element"/>: from the nearest ancestor with an id (or <c>body</c>/<c>head</c>/
    /// <c>html</c>), each step its tag and, among same-tag siblings, <c>:nth-of-type()</c>. At most
    /// <see cref="MaxDepth"/> steps; a deeper path is left unanchored rather than made long.
    /// </summary>
    public static string SelectorOf(IElement element)
    {
        var steps = new List<string>();
        for (var current = element; current is not null && steps.Count < MaxDepth; current = current.ParentElement)
        {
            if (current.Id is { Length: > 0 } id && IsIdentifier(id))
            {
                steps.Add("#" + id);
                break;
            }

            var tag = current.LocalName;
            if (tag is "html" or "body" or "head")
            {
                steps.Add(tag);
                break;
            }

            var siblings = current.ParentElement?.Children.Where(c => c.LocalName == tag).ToList();
            steps.Add(siblings is { Count: > 1 } ? $"{tag}:nth-of-type({siblings.IndexOf(current) + 1})" : tag);
        }
        steps.Reverse();
        return Cut(string.Join(" > ", steps), MaxSelectorLength);
    }

    /// <summary>An id usable after <c>#</c> without escaping: a letter, <c>_</c> or <c>-</c> first, then those or digits.</summary>
    private static bool IsIdentifier(string id)
        => id.Length <= 100
           && (char.IsAsciiLetter(id[0]) || id[0] is '_' || (id[0] is '-' && id.Length > 1 && !char.IsAsciiDigit(id[1])))
           && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static string? ExcerptOf(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;
        var builder = new StringBuilder(Math.Min(html.Length, MaxExcerptLength + 1));
        var space = false;
        foreach (var c in html)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }
            if (char.IsControl(c))
                continue;
            if (space)
            {
                builder.Append(' ');
                space = false;
            }
            builder.Append(c);
            if (builder.Length >= MaxExcerptLength)
                break;
        }
        return builder.Length == 0 ? null : Cut(builder.ToString(), MaxExcerptLength);
    }

    /// <summary>At most <paramref name="max"/> characters, never splitting a surrogate pair.</summary>
    private static string Cut(string value, int max)
        => value.Length <= max ? value : value[..(char.IsHighSurrogate(value[max - 1]) ? max - 1 : max)];
}
