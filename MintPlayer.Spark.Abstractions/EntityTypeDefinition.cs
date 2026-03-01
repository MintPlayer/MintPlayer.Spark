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
    public AttributeTab[] Tabs { get; set; } = [];
    public AttributeGroup[] Groups { get; set; } = [];
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
    /// When true, the attribute represents an array/collection of AsDetail objects (e.g., CarreerJob[]).
    /// When false (default), the attribute represents a single AsDetail object (e.g., Address?).
    /// </summary>
    public bool IsArray { get; set; }
    /// <summary>
    /// For array AsDetail attributes, controls how items are edited.
    /// "modal" (default) opens a dialog; "inline" edits directly in the table row.
    /// </summary>
    public string? EditMode { get; set; }
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
    /// <summary>
    /// References an AttributeGroup.Id to assign this attribute to a group.
    /// When null, the attribute is placed in a default (ungrouped) section.
    /// </summary>
    public Guid? Group { get; set; }
    /// <summary>
    /// Number of grid columns this attribute spans within a tab's column layout.
    /// Defaults to 1 when not specified.
    /// </summary>
    public int? ColumnSpan { get; set; }
}

public sealed class AttributeTab
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public TranslatedString? Label { get; set; }
    public int Order { get; set; }
    /// <summary>
    /// Number of columns for the grid layout within this tab.
    /// </summary>
    public int? ColumnCount { get; set; }
}

public sealed class AttributeGroup
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public TranslatedString? Label { get; set; }
    /// <summary>
    /// References an AttributeTab.Id to assign this group to a tab.
    /// When null, the group is placed on the first/default tab.
    /// </summary>
    public Guid? Tab { get; set; }
    public int Order { get; set; }
}
