namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// On a <b>property</b> (the marker form, no argument): declares that the containing type renders
/// as this property — "this type's breadcrumb is this value". The property may be stored or a
/// computed get-only member combining several fields (<c>[Breadcrumb] string FullName =&gt;
/// $"{FirstName} {LastName}"</c>); either way the value persists into the document JSON, so a
/// generated index can sort a complex-typed column by it and the resolver can render an embedded
/// object with it. <b>A computed getter must be null-safe</b>: it runs during serialization of
/// every save (and every session dirty-check), so a throwing getter makes the entity unsavable.
/// </summary>
/// <remarks>
/// On a <b>class</b> (the legacy template form): declares the display template, a string of
/// literal text and <c>{AttributeName}</c> placeholders. This form is being replaced by the
/// <c>"breadcrumb"</c> template in the entity's model JSON, which the synchronizer preserves and
/// validates; see the model documentation.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class BreadcrumbAttribute : Attribute
{
    public string? Template { get; }

    public BreadcrumbAttribute()
    {
    }

    public BreadcrumbAttribute(string template)
    {
        Template = template;
    }
}
