using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using System.Collections;
using System.Text;

namespace MintPlayer.Spark.Services.Breadcrumb;

/// <summary>
/// Renders an embedded AsDetail row's own breadcrumb from its <c>[Breadcrumb]</c> template.
/// Embedded rows have no document id, so they are not keyed in <see cref="BreadcrumbResult"/>;
/// instead we render the template in place, substituting scalar values read from the row and the
/// pre-resolved breadcrumb (by id) for each reference token. Reference targets are already fully
/// rendered strings in the supplied <see cref="BreadcrumbResult"/> — the resolver descended into
/// the AsDetail children and loaded them — so no recursion is needed here.
/// </summary>
internal static class EmbeddedBreadcrumbRenderer
{
    /// <returns>The rendered breadcrumb, or <c>null</c> when the type declares no breadcrumb
    /// (neither a template nor a <c>[Breadcrumb]</c>-marked property).</returns>
    public static string? Render(
        object entity,
        EntityTypeDefinition? def,
        BreadcrumbResult breadcrumbs,
        string referenceSeparator,
        Func<string, EntityTypeDefinition?>? defByClrType = null,
        int depth = 0)
    {
        if (depth >= 8)
            return string.Empty;

        if (string.IsNullOrEmpty(def?.Breadcrumb))
        {
            // Marker fallback: an unregistered (or template-less) embedded type renders as its
            // [Breadcrumb]-marked property.
            var marked = entity.GetType().GetBreadcrumbProperty();
            if (marked is null)
                return null;
            var value = AccessorCache.GetGetter(marked)(entity);
            if (value is null)
                return string.Empty;
            return Abstractions.Model.SparkModelShape.IsComplexType(value.GetType())
                ? Render(value, defByClrType?.Invoke(value.GetType().FullName ?? value.GetType().Name),
                    breadcrumbs, referenceSeparator, defByClrType, depth + 1) ?? string.Empty
                : value.ToString() ?? string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var token in BreadcrumbTemplate.Parse(def.Breadcrumb))
        {
            switch (token)
            {
                case LiteralToken literal:
                    sb.Append(literal.Text);
                    break;

                case FieldToken field:
                    var attr = def.Attributes.FirstOrDefault(a => a.Name == field.AttributeName);
                    if (attr is { DataType: "Reference" } && !string.IsNullOrEmpty(attr.ReferenceType))
                    {
                        var parts = ExtractIds(entity, field.AttributeName)
                            .Select(breadcrumbs.Get)
                            .Where(s => !string.IsNullOrEmpty(s));
                        sb.Append(string.Join(referenceSeparator, parts));
                    }
                    else if (attr is { DataType: "AsDetail", IsArray: false } && !string.IsNullOrEmpty(attr.AsDetailType))
                    {
                        // Embedded complex token: recurse into the embedded type's own breadcrumb
                        // instead of ToString()-ing the object (#273).
                        var child = ReadValue(entity, field.AttributeName);
                        if (child is not null)
                            sb.Append(Render(child, defByClrType?.Invoke(attr.AsDetailType!),
                                breadcrumbs, referenceSeparator, defByClrType, depth + 1) ?? string.Empty);
                    }
                    else
                    {
                        sb.Append(ReadValue(entity, field.AttributeName)?.ToString() ?? string.Empty);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    private static object? ReadValue(object entity, string propertyName)
    {
        var property = entity.GetType().GetCachedProperty(propertyName);
        return property is not null && property.CanRead ? AccessorCache.GetGetter(property)(entity) : null;
    }

    private static IEnumerable<string> ExtractIds(object entity, string propertyName)
    {
        var value = ReadValue(entity, propertyName);
        switch (value)
        {
            case null:
                yield break;
            case string s:
                if (!string.IsNullOrEmpty(s)) yield return s;
                yield break;
            case IEnumerable enumerable:
                foreach (var item in enumerable)
                {
                    var id = item?.ToString();
                    if (!string.IsNullOrEmpty(id)) yield return id;
                }
                yield break;
        }
    }
}
