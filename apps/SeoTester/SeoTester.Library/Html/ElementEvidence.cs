using AngleSharp.Dom;

namespace SeoTester.Html;

/// <summary>
/// Where on a page a finding is (<c>docs/spark_mcp_PRD.md</c> §4.7 gap 1): CSS selectors of up to
/// <see cref="MaxSelectors"/> offending elements, so an agent (or a person) can find the template that renders them.
/// </summary>
/// <remarks>
/// <b>Selectors only, no HTML excerpt</b> (spike S7, <c>docs/spark_mcp_spikes.md</c>): an excerpt of at most 300 characters
/// grew the stored page results by 40.7 % against 20.4 % for selectors alone, past the 25 % the PRD allows, and it would
/// store the page's body, which SeoTester never keeps (<c>docs/seo_analyzer_PRD.md</c> §12: no crawled text or markup is
/// stored; element selectors are kept with the latest page result, owner decision 2026-10-12). A selector names an element
/// by its tag, position and id: structure, not content. It is still the page's own markup and is fenced as data wherever an
/// agent reads it.
/// </remarks>
/// <param name="Selectors">The elements' selectors, at most <see cref="MaxSelectors"/>, each at most <see cref="MaxSelectorLength"/> characters.</param>
public sealed record ElementEvidence(IReadOnlyList<string> Selectors)
{
    public const int MaxSelectors = 5;
    public const int MaxSelectorLength = 200;

    /// <summary>Levels a selector climbs without meeting an id before it stops (the path is then unanchored).</summary>
    private const int MaxDepth = 6;

    /// <summary>The evidence of <paramref name="elements"/> in document order, or <see langword="null"/> when there is none.</summary>
    public static ElementEvidence? Of(IEnumerable<IElement> elements)
        => Of(elements.Take(MaxSelectors).Select(SelectorOf));

    /// <summary>Evidence from selectors something else already computed (axe-core's node targets), with the same caps.</summary>
    public static ElementEvidence? Of(IEnumerable<string>? selectors)
    {
        var list = (selectors ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => Cut(s.Trim(), MaxSelectorLength))
            .Distinct(StringComparer.Ordinal).Take(MaxSelectors).ToList();
        return list.Count == 0 ? null : new ElementEvidence(list);
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

    /// <summary>At most <paramref name="max"/> characters, never splitting a surrogate pair.</summary>
    private static string Cut(string value, int max)
        => value.Length <= max ? value : value[..(char.IsHighSurrogate(value[max - 1]) ? max - 1 : max)];
}
