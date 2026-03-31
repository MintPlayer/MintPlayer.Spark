namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Standalone model for a language definition.
/// Extracted from CultureConfiguration.Languages dictionary for use as a first-class entity.
/// </summary>
public sealed class LanguageDefinition
{
    /// <summary>
    /// The culture code (e.g., "en", "nl", "fr").
    /// </summary>
    public string Culture { get; set; } = string.Empty;

    /// <summary>
    /// The translated display name of the language (e.g., {"en": "English", "nl": "Engels"}).
    /// </summary>
    public TranslatedString? Name { get; set; }
}
