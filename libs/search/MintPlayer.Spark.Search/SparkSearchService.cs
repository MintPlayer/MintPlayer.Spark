using System.Text.RegularExpressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Search;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Search;

/// <summary>
/// <see cref="ISparkSearch"/> (<c>docs/spark_search_PRD.md</c> D2–D3). The provider proposes candidates,
/// <see cref="SearchReadPath"/> keeps the ones the caller may see and renders their labels, and namesakes get the
/// type's label, and a number among the namesakes of one type, so every string handed to a browser names one object.
/// </summary>
internal sealed partial class SparkSearchService : ISparkSearch
{
    /// <summary>Candidates asked per wanted hit: some are dropped by the security re-read.</summary>
    internal const int OverFetch = 3;

    [Inject] private readonly SearchReadPath readPath;
    [Inject] private readonly ISparkSearchProvider provider;
    [Inject] private readonly SparkSearchOptions options;

    private static readonly StringComparer LabelComparer = StringComparer.OrdinalIgnoreCase;

    public Task<IReadOnlyList<SparkSearchHit>> SuggestAsync(string? term, CancellationToken cancellationToken = default)
    {
        var text = SearchTerms.Normalize(term);
        return FindAsync(text, options.MaxSuggestions, prefix: SearchTerms.EndsInWord(text), cancellationToken);
    }

    public Task<IReadOnlyList<SparkSearchHit>> SearchAsync(string? term, int? take = null, CancellationToken cancellationToken = default)
    {
        var count = Math.Clamp(take ?? options.MaxResults, 1, options.MaxResults);
        return FindAsync(SearchTerms.Normalize(term), count, prefix: false, cancellationToken);
    }

    public async Task<SparkSearchHit?> ResolveAsync(string? text, CancellationToken cancellationToken = default)
    {
        var label = SearchTerms.Normalize(text);
        if (label.Length < options.MinTermLength)
            return null;
        var types = await readPath.AllowedTypesAsync(cancellationToken);
        if (types.Count == 0)
            return null;

        // 1. The label of exactly one visible object, as a suggestion without a suffix reads.
        var namesakes = await NamesakesAsync(label, types, cancellationToken);
        if (namesakes.Count == 1)
            return ToHit(namesakes[0], namesakes[0].BaseLabel);

        // 2. A disambiguated label: "<label> (<type label>)" or "<label> (<type label> <n>)". The suffix only counts when
        // it names a searchable type, so a title that ends in parentheses of its own still resolves through step 1.
        if (Suffix().Match(label) is { Success: true } match
            && types.FirstOrDefault(t => LabelComparer.Equals(t.Label, match.Groups["type"].Value)) is { } type)
        {
            var baseLabel = match.Groups["label"].Value;
            var ofType = (await NamesakesAsync(baseLabel, types, cancellationToken))
                .Where(h => h.Type.Name == type.Name)
                .OrderBy(h => h.Row.Id, NaturalIdComparer.Instance)
                .ToList();
            if (match.Groups["n"].Success)
            {
                var n = int.Parse(match.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (n >= 1 && n <= ofType.Count)
                    return ToHit(ofType[n - 1], label);
            }
            else if (ofType.Count == 1)
            {
                return ToHit(ofType[0], label);
            }
        }

        // 3. A term with exactly one visible match.
        var hits = await FindAsync(label, 2, prefix: false, cancellationToken);
        return hits.Count == 1 ? hits[0] : null;
    }

    private async Task<IReadOnlyList<SparkSearchHit>> FindAsync(string term, int take, bool prefix, CancellationToken cancellationToken)
    {
        if (term.Length < options.MinTermLength || SearchTerms.Words(term).Count == 0)
            return [];
        var types = await readPath.AllowedTypesAsync(cancellationToken);
        if (types.Count == 0)
            return [];

        var request = new SparkSearchRequest(
            term,
            types.Select(t => t.Name).ToList(),
            take * OverFetch,
            prefix,
            types.Where(t => t.Fuzzy).Select(t => t.Name).ToList());
        var visible = (await readPath.ReadAsync(await provider.SearchAsync(request, cancellationToken), cancellationToken))
            .Take(take)
            .ToList();

        return await LabelAsync(visible, types, cancellationToken);
    }

    /// <summary>
    /// D3: a label shared by several visible objects (not only the ones in this answer) gets a suffix. Namesakes of one
    /// type are numbered in natural id order; a type with one namesake gets its label only.
    /// </summary>
    private async Task<IReadOnlyList<SparkSearchHit>> LabelAsync(IReadOnlyList<VisibleHit> hits, IReadOnlyList<SearchableType> types, CancellationToken cancellationToken)
    {
        var labels = new Dictionary<(string Type, string Id), string>();
        foreach (var label in hits.Select(h => h.BaseLabel).Distinct(LabelComparer))
        {
            var namesakes = await NamesakesAsync(label, types, cancellationToken);
            // The hits themselves are always namesakes, even when a provider's label lookup misses one of them.
            namesakes = namesakes
                .Concat(hits.Where(h => LabelComparer.Equals(h.BaseLabel, label)))
                .DistinctBy(h => (h.Type.Name, h.Row.Id))
                .ToList();
            if (namesakes.Count < 2)
                continue;

            foreach (var group in namesakes.GroupBy(h => h.Type.Name))
            {
                var ordered = group.OrderBy(h => h.Row.Id, NaturalIdComparer.Instance).ToList();
                for (var i = 0; i < ordered.Count; i++)
                {
                    var h = ordered[i];
                    var suffix = ordered.Count == 1 ? h.Type.Label : $"{h.Type.Label} {i + 1}";
                    labels[(h.Type.Name, h.Row.Id)] = $"{h.BaseLabel} ({suffix})";
                }
            }
        }

        return hits
            .Select(h => ToHit(h, labels.GetValueOrDefault((h.Type.Name, h.Row.Id)) ?? h.BaseLabel))
            .ToList();
    }

    /// <summary>Every visible object whose label is <paramref name="label"/>.</summary>
    private async Task<IReadOnlyList<VisibleHit>> NamesakesAsync(string label, IReadOnlyList<SearchableType> types, CancellationToken cancellationToken)
    {
        var candidates = await provider.FindByLabelAsync(label, types.Select(t => t.Name).ToList(), cancellationToken);
        return (await readPath.ReadAsync(candidates, cancellationToken))
            .Where(h => LabelComparer.Equals(h.BaseLabel, label))
            .ToList();
    }

    private static SparkSearchHit ToHit(VisibleHit hit, string label)
        => new(
            hit.Type.Name,
            hit.Type.Label,
            hit.Row.Id,
            label,
            SparkEntityLinks.DetailPath(SparkEntityLinks.TypeSegment(hit.Type.Definition), hit.Row.Id, hit.Row.Breadcrumb));

    [GeneratedRegex(@"^(?<label>.+?) \((?<type>[^()]+?)(?: (?<n>[1-9][0-9]{0,5}))?\)$", RegexOptions.CultureInvariant)]
    private static partial Regex Suffix();
}
