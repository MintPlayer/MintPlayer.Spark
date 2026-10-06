using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.SourceGenerators.Json;

/// <summary>
/// Reads the supported language codes out of <c>App_Data/culture.json</c>.
/// <para>
/// A generator has no DI, so it cannot ask <c>CultureLoader</c> — the singleton that reads this file at
/// runtime — which languages exist. The file therefore has to arrive as an <c>AdditionalFiles</c> item and be
/// parsed here. <c>languages</c> is an array of codes (#467, D1); each language's display name is the
/// <c>translations.json</c> key <c>culture.languages.{code}</c>, which the generator does not need.
/// The array is read by pattern.
/// </para>
/// </summary>
internal static class CultureJsonReader
{
    /// <summary>
    /// The single language <c>CultureLoader</c> falls back to when the file is missing or unreadable. Matching
    /// it here keeps a build without <c>culture.json</c> generating the same shape the app would then serve.
    /// </summary>
    public const string DefaultLanguage = "en";

    private static readonly Regex LanguagesArray = new(
        "\"languages\"\\s*:\\s*\\[(?<items>[^\\]]*)\\]", RegexOptions.IgnoreCase);

    private static readonly Regex QuotedString = new("\"(?<v>[^\"]*)\"");

    /// <summary>
    /// Language codes in declaration order, or <c>["en"]</c> when the file is absent or declares no languages.
    /// <para>Declaration order matters: it decides the order of the generated per-language properties, and a
    /// reordering would otherwise churn the emitted source and the model for no reason.</para>
    /// <para>A malformed culture.json is the app's problem, reported by <c>CultureLoader</c> at runtime. Failing
    /// the build here would block work on an unrelated part of the project.</para>
    /// </summary>
    public static List<string> ReadLanguages(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [DefaultLanguage];

        var array = LanguagesArray.Match(json!);
        if (!array.Success)
            return [DefaultLanguage];

        var codes = new List<string>();
        foreach (Match code in QuotedString.Matches(array.Groups["items"].Value))
        {
            if (!string.IsNullOrWhiteSpace(code.Groups["v"].Value))
                codes.Add(code.Groups["v"].Value);
        }
        return codes.Count > 0 ? codes : [DefaultLanguage];
    }
}
