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
/// Display <b>templates</b> (literal text plus <c>{AttributeName}</c> placeholders, reference
/// tokens rendering the referenced entity's breadcrumb) live in the entity's model JSON as the
/// <c>"breadcrumb"</c> field — the synchronizer preserves an authored value, synthesizes a default
/// (preferring the marked property) and validates placeholders. The class-level template form of
/// this attribute was removed in #273.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class BreadcrumbAttribute : Attribute
{
}
