namespace SparkEditor.Entities;

public class AttributeDefinition
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string DataType { get; set; } = "string";
    public bool IsRequired { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsReadOnly { get; set; }
    public int Order { get; set; }
    public string? ShowedOn { get; set; }
    public string? Group { get; set; }
    public int ColumnSpan { get; set; } = 1;
    public string? Renderer { get; set; }
    public string? ReferenceType { get; set; }
    public string? AsDetailType { get; set; }
    public bool IsArray { get; set; }
    public string? EditMode { get; set; }
    public string? LookupReferenceType { get; set; }

    // Parent PO reference
    public string? PersistentObjectId { get; set; }
    public string? PersistentObjectName { get; set; }
}
