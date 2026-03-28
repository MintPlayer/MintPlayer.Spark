namespace SparkEditor.Entities;

public class TranslationDef
{
    public string? Id { get; set; }  // e.g., "TranslationDefs/{key}"
    public string Key { get; set; } = string.Empty;
    public string? Values { get; set; }  // TranslatedString JSON
}
