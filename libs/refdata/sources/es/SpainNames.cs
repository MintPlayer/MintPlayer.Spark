namespace MintPlayer.Spark.ReferenceData.Sources.Spain;

/// <summary>
/// INE's name conventions (PRD §4.4 "Names like Donostia/San Sebastián, Coruña, A"): official bilingual names joined by
/// a slash, articles and leading titles moved behind a comma for sorting, upper case in the Callejero.
/// </summary>
internal static class SpainNames
{
    private static readonly HashSet<string> Articles = new(StringComparer.OrdinalIgnoreCase)
    {
        "el", "la", "los", "las", "lo", "l'", "a", "o", "as", "os", "es", "sa", "ses", "els", "les", "en", "na", "s'", "ets",
    };

    private static readonly HashSet<string> Particles = new(StringComparer.OrdinalIgnoreCase)
    {
        "de", "del", "la", "las", "los", "el", "y", "e", "i", "da", "das", "do", "dos", "d'", "l'", "en", "a", "o", "as", "os", "al", "les", "els",
    };

    /// <summary>
    /// "Coruña, A" → "A Coruña"; "Palmas de Gran Canaria, Las" → "Las Palmas de Gran Canaria"; "Hospitalet de
    /// Llobregat, L'" → "L'Hospitalet de Llobregat"; "Asturias, Principado de" → "Principado de Asturias". A comma not
    /// followed by an article or a title ending in "de"/"del" is kept.
    /// </summary>
    public static string Uninvert(string name)
    {
        var comma = name.LastIndexOf(", ", StringComparison.Ordinal);
        if (comma < 0) return name.Trim();
        var head = name[..comma].Trim();
        var tail = name[(comma + 2)..].Trim();
        if (tail.EndsWith('\'')) return tail + head;
        if (Articles.Contains(tail) || tail.EndsWith(" de", StringComparison.OrdinalIgnoreCase) || tail.EndsWith(" del", StringComparison.OrdinalIgnoreCase))
            return tail + " " + head;
        return name.Trim();
    }

    /// <summary>"Donostia/San Sebastián" → the co-official form first, the Spanish one second (INE's order).</summary>
    public static IReadOnlyList<string> Split(string name)
        => [.. name.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Uninvert)];

    /// <summary>
    /// The Callejero's upper-case forms in title case: "ZARDO (EL)" → "El Zardo", "ELVIÑA (SAN VICENTE DE)" →
    /// "San Vicente de Elviña", "AGITI DE" → "de Agiti"... (the trailing particle INE uses for sorting moves to the front).
    /// Generated: the register has no mixed-case form.
    /// </summary>
    public static string TitleCase(string upper)
    {
        var text = upper.Trim();
        var open = text.LastIndexOf(" (", StringComparison.Ordinal);
        if (open > 0 && text.EndsWith(')'))
            text = text[(open + 2)..^1].Trim() + " " + text[..open].Trim();
        var words = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        // A trailing particle ("ADARRA DE", "AGITI DE") belongs in front of the name.
        if (words.Count > 1 && words[^1] is "de" or "del")
        {
            var particle = words[^1];
            words.RemoveAt(words.Count - 1);
            words.Insert(0, particle);
        }
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (i > 0 && Particles.Contains(word)) continue;
            if (i == 0 && Particles.Contains(word) && words.Count > 1 && word is "de" or "del") continue;
            words[i] = string.Concat(word.Split('-').Select(Capitalize).Select((p, k) => k == 0 ? p : "-" + p));
        }
        return string.Join(' ', words);
    }

    private static string Capitalize(string word)
    {
        if (word.Length == 0) return word;
        var apostrophe = word.IndexOf('\'');
        if (apostrophe >= 0 && apostrophe < word.Length - 1)
            return char.ToUpperInvariant(word[0]) + word[1..(apostrophe + 1)] + char.ToUpperInvariant(word[apostrophe + 1]) + word[(apostrophe + 2)..];
        return char.ToUpperInvariant(word[0]) + word[1..];
    }
}
