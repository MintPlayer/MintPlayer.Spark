namespace MintPlayer.Spark.Abstractions;

public sealed class EntityTypeDefinition
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public TranslatedString? Description { get; set; }
    public required string ClrType { get; set; }
    /// <summary>
    /// Optional URL-friendly alias for this entity type.
    /// Used as an alternative to the GUID in URLs (e.g., /po/car instead of /po/{guid}).
    /// If not set, auto-generated from Name by lowercasing.
    /// </summary>
    public string? Alias { get; set; }
    /// <summary>
    /// The CLR type name of the projection type used for RavenDB index queries.
    /// Set when a projection class has [FromIndex] attribute linking to an index for this entity.
    /// Example: "Demo.Data.VCar"
    /// </summary>
    public string? QueryType { get; set; }
    /// <summary>
    /// The name of the RavenDB index to use for list queries when QueryType is set.
    /// Derived from the IndexRegistry based on the index class name.
    /// Example: "Cars_Overview"
    /// </summary>
    public string? IndexName { get; set; }
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
    public TranslatedString? Label { get; set; }
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
    /// <summary>
    /// For LookupReference attributes, specifies the lookup reference type name.
    /// Example: "CarStatus", "CarBrand"
    /// </summary>
    public string? LookupReferenceType { get; set; }
    /// <summary>
    /// When false, this attribute exists only in the projection type (e.g., computed by index).
    /// Not present in the collection entity. Used for list views only.
    /// </summary>
    public bool? InCollectionType { get; set; }
    /// <summary>
    /// When false, this attribute exists only in the collection type (not projected by the index).
    /// Used for detail/edit views only.
    /// </summary>
    public bool? InQueryType { get; set; }
    /// <summary>
    /// Controls on which pages the attribute should be displayed.
    /// Query = shown in list views, PersistentObject = shown in detail/edit views.
    /// Default is both (Query | PersistentObject).
    /// </summary>
    public EShowedOn ShowedOn { get; set; } = EShowedOn.Query | EShowedOn.PersistentObject;
    public ValidationRule[] Rules { get; set; } = [];
}
