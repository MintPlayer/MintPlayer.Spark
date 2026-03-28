namespace SparkEditor.Entities;

public class PersistentObjectDefinition
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Label { get; set; }  // Serialized TranslatedString JSON
    public string? Breadcrumb { get; set; }  // DisplayFormat
    public string? ContextProperty { get; set; }
    public string? ClrType { get; set; }
    public string? QueryType { get; set; }
    public string? IndexName { get; set; }
    public string? Alias { get; set; }
    public string? DisplayAttribute { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsHidden { get; set; }
    public string? Description { get; set; }  // Serialized TranslatedString JSON

    // Back-reference to the source file
    public string? SourceFile { get; set; }
}
