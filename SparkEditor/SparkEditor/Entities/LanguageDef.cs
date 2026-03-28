namespace SparkEditor.Entities;

public class LanguageDef
{
    public string? Id { get; set; }  // e.g., "LanguageDefs/en"
    public string Culture { get; set; } = string.Empty;  // e.g., "en"
    public string? Name { get; set; }  // TranslatedString JSON
}
