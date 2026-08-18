using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Json;

/// <summary>
/// Reads the supported language codes out of <c>App_Data/culture.json</c>.
/// <para>
/// A generator has no DI, so it cannot ask <c>CultureLoader</c> — the singleton that reads this file at
/// runtime — which languages exist. The file therefore has to arrive as an <c>AdditionalFiles</c> item and be
/// parsed here. Only the keys of the <c>languages</c> object are needed; their values are
/// <c>TranslatedString</c> objects describing each language's own display name, which the generator ignores.
/// </para>
/// </summary>
internal static class CultureJsonReader
{
    /// <summary>
    /// The single language <c>CultureLoader</c> falls back to when the file is missing or unreadable. Matching
    /// it here keeps a build without <c>culture.json</c> generating the same shape the app would then serve.
    /// </summary>
    public const string DefaultLanguage = "en";

    /// <summary>
    /// Language codes in declaration order, or <c>["en"]</c> when the file is absent, unparsable, or declares
    /// no languages.
    /// <para>Declaration order matters: it decides the order of the generated per-language properties, and a
    /// reordering would otherwise churn the emitted source and the model for no reason.</para>
    /// </summary>
    public static List<string> ReadLanguages(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [DefaultLanguage];

        try
        {
            if (MiniJson.Parse(json!) is not JsonObject root)
                return [DefaultLanguage];

            foreach (var member in root.Members)
            {
                if (!string.Equals(member.Key, "languages", System.StringComparison.OrdinalIgnoreCase))
                    continue;

                if (member.Value is not JsonObject languages)
                    break;

                var codes = new List<string>();
                foreach (var language in languages.Members)
                {
                    if (!string.IsNullOrWhiteSpace(language.Key))
                        codes.Add(language.Key);
                }

                return codes.Count > 0 ? codes : [DefaultLanguage];
            }
        }
        catch (JsonParseException)
        {
            // A malformed culture.json is the app's problem, reported by CultureLoader at runtime. Failing the
            // build here would block work on an unrelated part of the project.
        }

        return [DefaultLanguage];
    }
}
