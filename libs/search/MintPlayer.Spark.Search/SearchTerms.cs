using System.Globalization;
using System.Text;

namespace MintPlayer.Spark.Search;

/// <summary>
/// How a typed term becomes words. Only letters and digits survive, so nothing a visitor types can reach a provider's
/// query syntax as an operator, wildcard or quote: the RavenDB provider passes each word as a <c>search()</c> argument and
/// the OpenSearch provider as a match query, never spliced into a query string.
/// </summary>
internal static class SearchTerms
{
    /// <summary>The most words a term contributes; the rest are ignored.</summary>
    public const int MaxWords = 8;

    /// <summary>The longest a term may be before it is cut.</summary>
    public const int MaxLength = 200;

    /// <summary>The term, trimmed and cut to <see cref="MaxLength"/>; empty for null.</summary>
    public static string Normalize(string? term)
    {
        var text = (term ?? "").Trim();
        return text.Length > MaxLength ? text[..MaxLength] : text;
    }

    /// <summary>The term's words, lower-cased and diacritics folded, at most <see cref="MaxWords"/>.</summary>
    public static IReadOnlyList<string> Words(string term)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in Fold(term))
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
                continue;
            }
            Flush();
            if (words.Count == MaxWords)
                return words;
        }
        Flush();
        return words;

        void Flush()
        {
            if (current.Length > 0 && words.Count < MaxWords)
                words.Add(current.ToString());
            current.Clear();
        }
    }

    /// <summary>Whether the term ends in a word still being typed (not in a space or punctuation).</summary>
    public static bool EndsInWord(string term) => term.Length > 0 && char.IsLetterOrDigit(term[^1]);

    private static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                folded.Append(c);
        return folded.ToString().Normalize(NormalizationForm.FormC);
    }
}
