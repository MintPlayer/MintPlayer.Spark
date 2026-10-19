using System.Globalization;
using System.Text;

namespace MintPlayer.Spark.ReferenceData;

/// <summary>
/// The search-key pipeline (PRD §4.10): keys are <b>data</b>, computed once at import by this one versioned function and
/// used again to fold the query, so the instance and every consumer compare the same bytes. Any change here bumps
/// <see cref="KeyVersion"/>, which the manifest records: the release is re-keyed visibly.
/// <list type="number">
/// <item>NFKC; unify punctuation (every apostrophe → <c>'</c>, every dash → <c>-</c>, drop the soft hyphen,
/// <c>. , / ( ) &amp;</c> → space);</item>
/// <item><c>ToLowerInvariant()</c> plus explicit İ/ı → i, ß/ẞ → ss, ς → σ (.NET has no full case folding);</item>
/// <item>non-Latin text keeps its native key (combining marks are never stripped there: が = か + U+3099). Machine
/// transliteration for true alphabets waits for RS21 and is not done;</item>
/// <item>fold Latin: NFD, drop marks only after a Latin letter or digit, NFC; map ø, ł, đ/ð, æ, œ, þ; extra keys for
/// de ä/ö/ü → ae/oe/ue and da/nb/sv å → aa;</item>
/// <item>tokenize on space, <c>'</c> and <c>-</c>;</item>
/// <item>emit spaced tokens, compact keys, article/elision-stripped keys and abbreviation-class keys
/// (sint/saint/sankt/st → <c>st</c>; onze-lieve-vrouw → <c>olv</c>).</item>
/// </list>
/// Ordinal operations only: no culture, no platform ICU (RS10).
/// </summary>
public static class RefSearchKeys
{
    public const int KeyVersion = 1;

    private static readonly HashSet<string> Articles = new(StringComparer.Ordinal)
    {
        "s", "l", "d", "le", "la", "les", "de", "den", "der", "het", "die", "das", "du", "des", "t",
    };

    private static readonly HashSet<string> SaintClass = new(StringComparer.Ordinal)
    {
        "sint", "saint", "sainte", "sankt", "st", "ste", "san", "santa", "santo",
    };

    /// <summary>Throws when the runtime cannot decompose (invariant-globalization mode may return input unchanged).</summary>
    public static void AssertNormalizationAvailable()
    {
        if ("é".Normalize(NormalizationForm.FormD).Length != 2)
            throw new InvalidOperationException(
                "Unicode normalization is unavailable (InvariantGlobalization?): reference-data search keys would differ from the shipped ones.");
    }

    /// <summary>The keys of one place, postal locality or street, from all its names.</summary>
    public static SearchKeys For(IEnumerable<string> names)
    {
        var tokens = new SortedSet<string>(StringComparer.Ordinal);
        var compact = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            foreach (var variant in Variants(name))
            {
                var parts = Tokenize(variant);
                foreach (var part in parts) tokens.Add(part);

                compact.Add(string.Concat(parts));
                var stripped = parts.Where(p => !Articles.Contains(p)).ToList();
                if (stripped.Count > 0 && stripped.Count != parts.Count)
                    compact.Add(string.Concat(stripped));

                var abbreviated = Abbreviate(parts);
                if (!abbreviated.SequenceEqual(parts))
                {
                    foreach (var part in abbreviated) tokens.Add(part);
                    compact.Add(string.Concat(abbreviated));
                }
            }
        }
        tokens.Remove(string.Empty);
        compact.Remove(string.Empty);
        return new SearchKeys { KeyVersion = KeyVersion, Tokens = [.. tokens], Compact = [.. compact] };
    }

    /// <summary>A query folded the same way: its tokens (in order) and its compact key.</summary>
    public static (IReadOnlyList<string> Tokens, string Compact) Query(string query)
    {
        var parts = Tokenize(Fold(query));
        return (parts, string.Concat(parts));
    }

    /// <summary>Steps 1–4: the folded text, separators kept.</summary>
    public static string Fold(string text)
    {
        var s = Punctuation(text.Normalize(NormalizationForm.FormKC));
        s = CaseFold(s);
        return IsLatinOrCommon(s) ? FoldLatin(s) : s.Normalize(NormalizationForm.FormC);
    }

    /// <summary>The folded tokens: split on space, apostrophe and hyphen.</summary>
    public static List<string> Tokenize(string folded)
        => [.. folded.Split([' ', '\'', '-'], StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>The name folded, plus the German and Nordic transcription variants where they differ.</summary>
    private static IEnumerable<string> Variants(string name)
    {
        var lowered = CaseFold(Punctuation(name.Normalize(NormalizationForm.FormKC))).Normalize(NormalizationForm.FormC);
        var seen = new HashSet<string>(StringComparer.Ordinal) { Fold(name) };
        yield return Fold(name);
        foreach (var variant in new[]
                 {
                     lowered.Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue"),
                     lowered.Replace("å", "aa").Replace("ø", "oe").Replace("æ", "ae"),
                 })
        {
            var folded = Fold(variant);
            if (seen.Add(folded))
                yield return folded;
        }
    }

    private static List<string> Abbreviate(List<string> parts)
    {
        var result = new List<string>(parts.Count);
        for (var i = 0; i < parts.Count; i++)
        {
            if (i + 2 < parts.Count && parts[i] == "onze" && parts[i + 1] == "lieve" && parts[i + 2] is "vrouw" or "heer")
            {
                result.Add(parts[i + 2] == "vrouw" ? "olv" : "olh");
                i += 2;
                continue;
            }
            result.Add(SaintClass.Contains(parts[i]) ? "st" : parts[i]);
        }
        return result;
    }

    private static string Punctuation(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '­': break; // soft hyphen
                case '‘' or '’' or '‛' or 'ʼ' or '`' or '´' or '′': builder.Append('\''); break;
                case '‐' or '‑' or '‒' or '–' or '—' or '―' or '−': builder.Append('-'); break;
                case '.' or ',' or '/' or '(' or ')' or '&' or '"' or ';' or ':': builder.Append(' '); break;
                default: builder.Append(char.IsWhiteSpace(c) ? ' ' : c); break;
            }
        }
        return builder.ToString().Trim();
    }

    private static string CaseFold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case 'İ' or 'ı': builder.Append('i'); break;
                case 'ß' or 'ẞ': builder.Append("ss"); break;
                case 'ς': builder.Append('σ'); break;
                default: builder.Append(char.ToLowerInvariant(c)); break;
            }
        }
        return builder.ToString();
    }

    private static string FoldLatin(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var previousLatin = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                if (!previousLatin) builder.Append(c);
                continue;
            }
            previousLatin = IsLatinLetterOrDigit(c);
            builder.Append(c switch
            {
                'ø' => "o",
                'ł' => "l",
                'đ' or 'ð' => "d",
                'æ' => "ae",
                'œ' => "oe",
                'þ' => "th",
                _ => c.ToString(),
            });
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool IsLatinLetterOrDigit(char c)
        => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or >= 'À' and <= 'ɏ';

    private static bool IsLatinOrCommon(string text)
        => text.All(c => !char.IsLetter(c) || IsLatinLetterOrDigit(c) || c is >= 'Ḁ' and <= 'ỿ');
}
