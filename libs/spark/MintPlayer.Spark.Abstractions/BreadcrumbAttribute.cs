namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Declares the breadcrumb template for an entity type. The template is a string of
/// literal text and <c>{AttributeName}</c> placeholders. A placeholder that names a
/// scalar attribute renders its value; one that names a <see cref="ReferenceAttribute"/>
/// reference renders the <em>referenced entity's</em> breadcrumb (resolved recursively).
/// </summary>
/// <example><c>[Breadcrumb("{ParkedCar} ({Coordinates})")]</c></example>
/// <remarks>
/// Templates are authored against the <b>collection</b> type's property names, so the same
/// breadcrumb resolves identically on the collection-backed detail page and the
/// projection-backed query list. Use <c>{{</c> / <c>}}</c> for literal braces.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class BreadcrumbAttribute : Attribute
{
    public string Template { get; }

    public BreadcrumbAttribute(string template)
    {
        Template = template;
    }
}
