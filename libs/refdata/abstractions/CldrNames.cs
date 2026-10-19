using System.Text.Json;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.ReferenceData;

/// <summary>
/// A pinned CLDR subset (Q-R18, RD-DEF 10): territory names for ISO 3166-1 alpha-2 codes in de, en, fr and nl, so
/// server-side output (mails, PDFs) names countries without a browser and without an instance (free-text mode too).
/// Browsers use <c>Intl.DisplayNames</c> instead (K11). Includes Unicode CLDR data, Copyright © 1991-2026 Unicode, Inc.,
/// under the Unicode License v3 (<c>Cldr/CLDR-LICENSE.txt</c>, shipped beside it).
/// </summary>
public static class CldrNames
{
    private static readonly Lazy<CldrTerritories> Data = new(Load);

    /// <summary>The CLDR release the subset was cut from.</summary>
    public static string Version => Data.Value.CldrVersion;

    /// <summary>The territory's name in <paramref name="language"/> (RFC 4647 lookup, then English), or the code itself.</summary>
    public static string Territory(string code, string? language)
    {
        var upper = code.ToUpperInvariant();
        foreach (var candidate in LanguageTag.LookupChain(language).Append("en"))
            if (Data.Value.Names.TryGetValue(candidate.ToLowerInvariant(), out var names) && names.TryGetValue(upper, out var name))
                return name;
        return upper;
    }

    /// <summary>Every alpha-2 code the subset names.</summary>
    public static IEnumerable<string> Codes => Data.Value.Names.TryGetValue("en", out var en) ? en.Keys : [];

    private static CldrTerritories Load()
    {
        using var stream = typeof(CldrNames).Assembly.GetManifestResourceStream("Cldr/territories.json")
            ?? throw new InvalidOperationException("The embedded CLDR territory names are missing.");
        return JsonSerializer.Deserialize<CldrTerritories>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The embedded CLDR territory names are empty.");
    }

    private sealed class CldrTerritories
    {
        public string CldrVersion { get; set; } = string.Empty;
        public Dictionary<string, Dictionary<string, string>> Names { get; set; } = [];
    }
}
