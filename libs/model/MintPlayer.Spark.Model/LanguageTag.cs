namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// RFC 5646 language tags (<c>en</c>, <c>nl-BE</c>, <c>zh-Hant</c>, <c>sr-Latn-RS</c>) and the RFC 4647
/// "lookup" truncation a <see cref="TranslatedString"/> falls back through.
/// <para>
/// Only well-formedness is checked (the RFC 5646 §2.1 syntax), not registry validity: a tag the IANA
/// registry does not know yet is still a tag. Grandfathered tags (<c>i-klingon</c>) are not accepted.
/// </para>
/// <para>
/// The client mirrors <see cref="LookupChain"/> and the comparison rule in
/// <c>@mintplayer/ng-spark/models</c> (<c>translated-string.ts</c>); the shared fixture
/// <c>translated-string.fixture.json</c> keeps the two identical.
/// </para>
/// </summary>
public static class LanguageTag
{
    /// <summary>Whether <paramref name="tag"/> follows the RFC 5646 §2.1 syntax.</summary>
    public static bool IsWellFormed(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return false;

        var parts = tag.Split('-');
        foreach (var part in parts)
            if (part.Length is 0 or > 8 || !part.All(char.IsAsciiLetterOrDigit)) return false;

        if (IsPrivateUseSingleton(parts[0])) return parts.Length > 1;

        // language: 2-3 letters (+ up to three 3-letter extlangs), 4 letters (reserved) or 5-8 letters.
        if (parts[0].Length < 2 || !AllLetters(parts[0])) return false;
        var i = 1;
        if (parts[0].Length <= 3)
            for (var n = 0; n < 3 && i < parts.Length && parts[i].Length == 3 && AllLetters(parts[i]); n++) i++;

        if (i < parts.Length && IsScript(parts[i])) i++;
        if (i < parts.Length && IsRegion(parts[i])) i++;
        while (i < parts.Length && IsVariant(parts[i])) i++;

        var singletons = new HashSet<char>();
        while (i < parts.Length && parts[i].Length == 1 && !IsPrivateUseSingleton(parts[i]))
        {
            if (!singletons.Add(char.ToLowerInvariant(parts[i][0]))) return false;
            i++;
            var subtags = 0;
            while (i < parts.Length && parts[i].Length >= 2) { i++; subtags++; }
            if (subtags == 0) return false;
        }

        if (i < parts.Length && IsPrivateUseSingleton(parts[i])) return i + 1 < parts.Length;
        return i == parts.Length;
    }

    /// <summary>
    /// The RFC 5646 §2.1.1 case of <paramref name="tag"/>: language lowercase, script titlecase
    /// (<c>Hant</c>), region uppercase (<c>BE</c>), everything else — and everything after a
    /// singleton — lowercase. Stored entries always carry this form, which is what lets an index match
    /// a tag with plain equality.
    /// </summary>
    /// <exception cref="ArgumentException">The tag is not well-formed.</exception>
    public static string Canonicalize(string tag)
    {
        if (!IsWellFormed(tag))
            throw new ArgumentException($"'{tag}' is not a well-formed RFC 5646 language tag (e.g. 'en', 'nl-BE', 'zh-Hant').", nameof(tag));

        var parts = tag.Split('-');
        var afterSingleton = false;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (i > 0 && !afterSingleton && part.Length == 4 && AllLetters(part))
                parts[i] = char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant();
            else if (i > 0 && !afterSingleton && part.Length == 2 && AllLetters(part))
                parts[i] = part.ToUpperInvariant();
            else
                parts[i] = part.ToLowerInvariant();

            if (part.Length == 1) afterSingleton = true;
        }
        return string.Join('-', parts);
    }

    /// <summary>Tags compare case-insensitively (RFC 5646 §2.1.1).</summary>
    public static bool AreEqual(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The RFC 4647 §3.4 lookup chain of one language range: the range itself, then progressively
    /// shorter by dropping the last subtag, also dropping a singleton left at the end
    /// (<c>zh-Hant-TW-x-a</c> → <c>zh-Hant-TW</c> → <c>zh-Hant</c> → <c>zh</c>). The wildcard
    /// <c>*</c> and blanks yield nothing.
    /// </summary>
    public static IEnumerable<string> LookupChain(string? range)
    {
        if (string.IsNullOrWhiteSpace(range) || range.Trim() == "*") yield break;

        var current = range.Trim();
        while (current.Length > 0)
        {
            yield return current;
            var dash = current.LastIndexOf('-');
            if (dash < 0) yield break;
            current = current[..dash];
            if (current.Length >= 2 && current[^2] == '-') current = current[..^2];
        }
    }

    private static bool IsPrivateUseSingleton(string part) => part is "x" or "X";
    private static bool IsScript(string part) => part.Length == 4 && AllLetters(part);
    private static bool IsRegion(string part)
        => (part.Length == 2 && AllLetters(part)) || (part.Length == 3 && part.All(char.IsAsciiDigit));
    private static bool IsVariant(string part)
        => part.Length is >= 5 and <= 8 || (part.Length == 4 && char.IsAsciiDigit(part[0]));
    private static bool AllLetters(string part) => part.All(char.IsAsciiLetter);
}
