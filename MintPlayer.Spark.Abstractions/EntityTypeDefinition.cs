namespace MintPlayer.Spark.Abstractions;

public sealed class EntityTypeDefinition
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public required string ClrType { get; set; }
    public string? DisplayAttribute { get; set; }
    public EntityAttributeDefinition[] Attributes { get; set; } = [];
}

public sealed class EntityAttributeDefinition
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Label { get; set; }
    public string DataType { get; set; } = "string";
    public bool IsRequired { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsReadOnly { get; set; }
    public int Order { get; set; }
    public string? Query { get; set; }
    /// <summary>
    /// For reference attributes, specifies the target entity type's CLR type name.
    /// </summary>
    public string? ReferenceType { get; set; }
    /// <summary>
    /// For embedded attributes, specifies the embedded entity type's CLR type name.
    /// </summary>
    public string? EmbeddedType { get; set; }
    public ValidationRule[] Rules { get; set; } = [];
}
