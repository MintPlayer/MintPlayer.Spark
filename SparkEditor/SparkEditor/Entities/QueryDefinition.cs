namespace SparkEditor.Entities;

public class QueryDefinition
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? Source { get; set; }
    public string? Alias { get; set; }
    public string? EntityType { get; set; }
    public string? Description { get; set; }
    public bool IsHidden { get; set; }
    public string? RenderMode { get; set; }

    // Back-reference
    public string? PersistentObjectName { get; set; }
}
