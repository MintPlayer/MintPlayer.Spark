namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Marks a property as a contextual lookup reference whose dropdown options
/// are derived from a sibling AsDetail array on the parent entity.
/// The frontend resolves options locally from the parent form data (no HTTP call needed).
/// </summary>
/// <example>
/// <code>
/// // TargetColumnOptionId gets a dropdown populated from parent GitHubProject.Columns[]
/// [ContextualLookupReference("Columns", "OptionId", "Name")]
/// public string? TargetColumnOptionId { get; set; }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ContextualLookupReferenceAttribute : Attribute
{
    /// <summary>
    /// Name of the AsDetail array property on the parent entity that provides the option values.
    /// </summary>
    public string SourceProperty { get; }

    /// <summary>
    /// Property name on the source element type that provides the option key (stored value).
    /// </summary>
    public string KeyProperty { get; }

    /// <summary>
    /// Property name on the source element type that provides the display label.
    /// </summary>
    public string DisplayProperty { get; }

    public ContextualLookupReferenceAttribute(
        string sourceProperty, string keyProperty, string displayProperty)
    {
        SourceProperty = sourceProperty;
        KeyProperty = keyProperty;
        DisplayProperty = displayProperty;
    }
}
