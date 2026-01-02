namespace MintPlayer.Spark.Abstractions;

public sealed class EntityTypeDefinition
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public required string ClrType { get; set; }
    /// <summary>
    /// Template string with {PropertyName} placeholders for building a formatted display value.
    /// Example: "{Street}, {PostalCode} {City}"
    /// </summary>
    public string? DisplayFormat { get; set; }
    /// <summary>
    /// (Fallback) Single attribute name to use as display value when DisplayFormat is not specified.
    /// </summary>
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
    /// For AsDetail attributes, specifies the nested entity type's CLR type name.
    /// </summary>
    public string? AsDetailType { get; set; }
    public ValidationRule[] Rules { get; set; } = [];
}
