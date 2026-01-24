namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Specifies that this property contains a reference to a lookup value by name.
/// Use this attribute when the entity is in a separate library and cannot reference
/// the lookup type directly. The framework will resolve the lookup type by name at runtime.
/// </summary>
/// <example>
/// <code>
/// // In library project (cannot reference app-specific lookup types)
/// public class Car
/// {
///     [LookupReferenceName("CarStatus")]
///     public string? Status { get; set; }
///
///     [LookupReferenceName("CarBrand")]
///     public string? Brand { get; set; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property)]
public sealed class LookupReferenceNameAttribute : Attribute
{
    /// <summary>
    /// The name of the lookup reference type.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Creates a new LookupReferenceNameAttribute with the specified lookup name.
    /// </summary>
    /// <param name="name">The name of the lookup reference type (e.g., "CarStatus", "CarBrand").</param>
    public LookupReferenceNameAttribute(string name)
    {
        Name = name;
    }
}
