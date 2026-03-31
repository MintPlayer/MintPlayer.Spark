namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Standalone model for a translation key-value entry.
/// Extracted from the translations.json flat dictionary for use as a first-class entity.
/// </summary>
public sealed class TranslationEntry
{
    /// <summary>
    /// The translation key (e.g., "save", "cancel", "error.notFound").
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The translated values per language.
    /// </summary>
    public TranslatedString? Values { get; set; }
}
