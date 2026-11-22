using System.Reflection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Abstractions.Reflection;

namespace MintPlayer.Spark.Services;

/// <summary>
/// #700: a model's file attributes are checked at startup, naming every problem, rather than failing
/// on the first save or download:
/// <list type="bullet">
/// <item>a malformed <c>fileOptions</c> (wildcards, SVG on an Image, a <c>maxCount</c> on a single file, …);</item>
/// <item>a stored-file attribute (<see cref="SparkFile"/>/<see cref="SparkImage"/>) without
/// <c>spark.AddFiles()</c> (D2b);</item>
/// <item>a stored file inside an AsDetail row of an AsDetail row. One level is supported (M10: the claim is bound to the
/// document and <c>Parent.Field</c>); deeper is not.</item>
/// </list>
/// </summary>
internal static class SparkFileAttributeStartupCheck
{
    public static IReadOnlyList<string> Unmet(IModelLoader modelLoader, bool filesEnabled)
    {
        var problems = new List<string>();

        foreach (var type in modelLoader.GetEntityTypes())
        {
            var clrType = SparkTypeResolver.ResolveClrType(type.ClrType);

            foreach (var attribute in type.Attributes)
            {
                var where = $"{type.Name}.{attribute.Name}";
                problems.AddRange(SparkFileOptions.ShapeErrors(attribute.FileOptions, where, attribute.DataType, attribute.IsArray));

                var property = clrType?.GetCachedProperty(attribute.Name);
                if (property is null) continue;

                if (!filesEnabled && EntityMapper.IsFileProperty(property))
                    problems.Add($"'{where}' is a file attribute, but file attributes are not enabled. Call spark.AddFiles() (package MintPlayer.Spark.Files).");

                // One level of AsDetail rows may hold files (M10): bound to the document and Parent.Field. A row of a row
                // cannot, because a two-segment path is all prepare, the claim and the download resolve.
                if (attribute.DataType == "AsDetail")
                    foreach (var nested in FilePropertiesOfNestedRows(RowType(property.PropertyType), [RowType(property.PropertyType)]))
                        problems.Add($"'{where}' is an AsDetail whose rows hold an AsDetail with the file attribute '{nested}'. Files are supported one AsDetail level deep only; reference a separate entity instead.");
            }
        }

        return problems;
    }

    private static Type RowType(Type propertyType)
    {
        var underlying = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        return SparkModelShape.GetCollectionElementType(underlying) ?? underlying;
    }

    /// <summary>The file properties of the AsDetail rows inside <paramref name="rowType"/>'s rows, at any depth.</summary>
    private static IEnumerable<string> FilePropertiesOfNestedRows(Type rowType, HashSet<Type> visited)
    {
        foreach (var row in rowType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (SparkModelShape.GetDataType(row.PropertyType) != "AsDetail")
                continue;
            var nestedType = RowType(row.PropertyType);
            if (!visited.Add(nestedType))
                continue;
            foreach (var property in nestedType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (EntityMapper.IsFileProperty(property))
                    yield return $"{nestedType.Name}.{property.Name}";
            foreach (var deeper in FilePropertiesOfNestedRows(nestedType, visited))
                yield return deeper;
        }
    }
}
