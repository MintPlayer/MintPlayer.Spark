using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.ReferenceData;

/// <summary>
/// As-of resolution (PRD §4.2). "Current" is never stored: it is this function of a date.
/// <code>
/// Resolve(p, d):
///   if p.Lifetime contains d            -> [p]
///   else union over p.SupersededBy where e.Date &lt;= d of Resolve(e.Id, d)
///   (sorted by id; Ambiguous = more than one result)
/// </code>
/// A pre-split reference resolves <see cref="AsOfResult.Ambiguous"/> through <see cref="SuccessionShare.Part"/> edges;
/// the street disambiguates. An <c>asOf</c> before the country's "history known since" is reported, never guessed.
/// </summary>
public static class RefAsOf
{
    /// <summary>The place(s) <paramref name="place"/> stands for on <paramref name="day"/>.</summary>
    /// <param name="load">Loads a place by id (from a session, a release, or memory).</param>
    public static async Task<AsOfResult> ResolveAsync(RefPlace place, DateOnly day, Func<string, Task<RefPlace?>> load, DateOnly? historyKnownSince = null)
    {
        var found = new SortedDictionary<string, RefPlace>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var partial = false;
        await Walk(place, false);
        return new AsOfResult(
            [.. found.Values],
            Ambiguous: found.Count > 1 || (partial && found.Count != 1),
            BeforeHistory: historyKnownSince is { } since && day < since);

        async Task Walk(RefPlace current, bool viaPart)
        {
            if (current.Id is null || !visited.Add(current.Id))
                return;
            if (current.Lifetime.Contains(day))
            {
                found[current.Id] = current;
                partial |= viaPart;
                return;
            }
            // Before the place existed there is nothing to resolve to; after it ended, follow its successors.
            if (current.Lifetime.From is { } from && day < from)
                return;
            foreach (var edge in current.SupersededBy.Where(e => e.Date <= day).OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase))
            {
                if (await load(edge.Id) is { } next)
                    await Walk(next, viaPart || edge.Share == SuccessionShare.Part);
            }
        }
    }

    /// <summary>The segment of <paramref name="place"/> covering <paramref name="day"/>, or <see langword="null"/>.</summary>
    public static PlaceSegment? SegmentAt(RefPlace place, DateOnly day)
        => place.Segments.FirstOrDefault(s => DateKeys.Covers(s.FromKey, s.ToKey, day));

    /// <summary>The period of <paramref name="place"/> covering <paramref name="day"/>, or <see langword="null"/>.</summary>
    public static PlacePeriod? PeriodAt(RefPlace place, DateOnly day)
        => place.Periods.FirstOrDefault(p => (p.From is null || p.From.Value <= day) && (p.To is null || day < p.To.Value));

    /// <summary>The street's period covering <paramref name="day"/>: "which municipality was this address in on X?".</summary>
    public static StreetPeriod? PeriodAt(RefStreet street, DateOnly day)
        => street.Periods.FirstOrDefault(p => (p.From is null || p.From.Value <= day) && (p.To is null || day < p.To.Value));
}

/// <summary>The outcome of <see cref="RefAsOf.ResolveAsync"/>.</summary>
/// <param name="Places">Sorted by id.</param>
/// <param name="Ambiguous">More than one place, or a part of a split unit.</param>
/// <param name="BeforeHistory">The date predates the country's "history known since": the answer may be incomplete.</param>
public sealed record AsOfResult(IReadOnlyList<RefPlace> Places, bool Ambiguous, bool BeforeHistory);

/// <summary>
/// The display rule (RD7, PRD §4.9), the same on the server and in the TypeScript client, for a place at
/// <c>asOf</c> in UI language <c>u</c>:
/// <list type="number">
/// <item><b>local</b>: the official names valid at <c>asOf</c> in the area's official languages, in the authority's
/// order, joined with " - " (Q-R13): "Rue de la Loi - Wetstraat";</item>
/// <item><b>ui</b>: if <c>u</c> is an official language there, that language's official name alone;</item>
/// <item><b>secondary</b>: otherwise the exonym in <c>u</c> after the local name: "Antwerpen (Anvers)";</item>
/// <item><b>latin</b>: for a non-Latin local name, the authoritative romanization (never a machine one, Q-R11);</item>
/// <item>fallback: <c>u</c> through RFC 4647 truncation, then any official name, then the first.</item>
/// </list>
/// </summary>
public static class PlaceNames
{
    public const string Separator = " - ";

    public static PlaceDisplayName Display(TranslatedString names, IReadOnlyList<string> officialLanguages, string? uiLanguage, DateOnly asOf)
    {
        var current = names.Entries.Where(e => e.IsCurrent(asOf)).ToList();
        bool IsOfficial(TranslatedStringEntry e) => e.Kind is null or TranslatedStringKind.Official;

        var local = new List<string>();
        foreach (var lang in officialLanguages)
            foreach (var entry in current.Where(e => IsOfficial(e) && LanguageTag.AreEqual(e.Tag, lang)).Take(1))
                if (!local.Contains(entry.Value, StringComparer.Ordinal))
                    local.Add(entry.Value);
        if (local.Count == 0 && current.FirstOrDefault(IsOfficial) is { } anyOfficial)
            local.Add(anyOfficial.Value);
        if (local.Count == 0 && (current.FirstOrDefault() ?? names.Entries.FirstOrDefault()) is { } first)
            local.Add(first.Value);
        var localText = string.Join(Separator, local);

        string? ui = null;
        string display = localText;
        if (!string.IsNullOrWhiteSpace(uiLanguage))
        {
            var chain = LanguageTag.LookupChain(uiLanguage).ToList();
            var officialHere = officialLanguages.Any(l => chain.Any(c => LanguageTag.AreEqual(c, l)));
            if (officialHere)
            {
                ui = chain.Select(c => current.FirstOrDefault(e => IsOfficial(e) && LanguageTag.AreEqual(e.Tag, c))?.Value)
                    .FirstOrDefault(v => v is not null);
                display = ui ?? localText;
            }
            else
            {
                var exonym = chain.Select(c => current.FirstOrDefault(e => e.Kind == TranslatedStringKind.Exonym && LanguageTag.AreEqual(e.Tag, c))?.Value)
                    .FirstOrDefault(v => v is not null);
                if (exonym is not null && !local.Contains(exonym, StringComparer.Ordinal))
                {
                    ui = exonym;
                    display = $"{localText} ({exonym})";
                }
            }
        }

        string? latin = null;
        if (localText.Any(c => char.IsLetter(c) && c > 'ɏ'))
            latin = current.FirstOrDefault(e => e.Romanization == RomanizationOrigin.Authoritative)?.Value;

        return new PlaceDisplayName(localText, ui, latin, display);
    }
}

/// <summary>The names the API returns; the formatter picks per use. Stored addresses keep <see cref="Local"/>.</summary>
public sealed record PlaceDisplayName(string Local, string? Ui, string? Latin, string Display);
