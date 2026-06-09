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
    /// <returns>The rendered breadcrumb, or <c>null</c> when the type has no template.</returns>
    public static string? Render(object entity, EntityTypeDefinition def, BreadcrumbResult breadcrumbs, string referenceSeparator)
    {
        if (string.IsNullOrEmpty(def.Breadcrumb))
            return null;

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
