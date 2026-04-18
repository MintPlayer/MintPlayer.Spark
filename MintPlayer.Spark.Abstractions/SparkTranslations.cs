using System.Collections.Generic;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Holds the merged translations aggregated by <c>HostTranslationsAggregatorGenerator</c>.
/// The generated <c>SparkTranslationsRegistry</c> in the host's assembly populates this
/// via a module initializer before <c>Main</c> runs.
/// </summary>
public static class SparkTranslations
{
    private static IReadOnlyDictionary<string, TranslatedString> all = new Dictionary<string, TranslatedString>();

    public static IReadOnlyDictionary<string, TranslatedString> All => all;

    public static void Register(IReadOnlyDictionary<string, TranslatedString> translations)
    {
        all = translations ?? new Dictionary<string, TranslatedString>();
    }
}
