using System.Reflection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Files.Services;

/// <summary>
/// The file attribute an upload, a download or a scan names on a type (#700 M10): a top-level attribute (<c>Photos</c>), or a
/// file attribute of an AsDetail row, one level deep, as <c>Parent.Field</c> (<c>ServiceEntries.Invoice</c>). Every place that
/// reads the name resolves it here, so a path means the same to prepare, commit, the deferred scan and the download.
/// </summary>
internal static class SparkFileAttributePath
{
    /// <summary>A resolved file attribute.</summary>
    /// <param name="Attribute">The file attribute's definition (on the row type for a nested path), with its <c>fileOptions</c>.</param>
    /// <param name="Property">Its CLR property (on the row type for a nested path).</param>
    /// <param name="Parent">The AsDetail attribute on the root type, or <see langword="null"/> for a top-level attribute.</param>
    /// <param name="ParentProperty">The AsDetail property on the root type, or <see langword="null"/>.</param>
    /// <param name="RowType">The row type's definition, or <see langword="null"/> for a top-level attribute.</param>
    internal sealed record Resolved(
        EntityAttributeDefinition Attribute,
        PropertyInfo Property,
        EntityAttributeDefinition? Parent,
        PropertyInfo? ParentProperty,
        EntityTypeDefinition? RowType);

    /// <summary>The file attribute <paramref name="path"/> names on <paramref name="rootType"/>, or null when it names none.</summary>
    internal static Resolved? Resolve(IModelLoader modelLoader, EntityTypeDefinition rootType, string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var rootClr = SparkTypeResolver.ResolveClrType(rootType.ClrType);
        if (rootClr is null)
            return null;

        var parts = path.Split('.');
        if (parts.Length == 1)
        {
            return FileAttribute(rootType, rootClr, parts[0]) is { } top
                ? new Resolved(top.Attribute, top.Property, null, null, null)
                : null;
        }
        if (parts.Length != 2)
            return null;

        var parent = rootType.Attributes.FirstOrDefault(a => string.Equals(a.Name, parts[0], StringComparison.Ordinal));
        var parentProperty = rootClr.GetCachedProperty(parts[0]);
        if (parent is null || parent.DataType != "AsDetail" || parentProperty is null)
            return null;

        var underlying = Nullable.GetUnderlyingType(parentProperty.PropertyType) ?? parentProperty.PropertyType;
        var rowClr = SparkModelShape.GetCollectionElementType(underlying) ?? underlying;
        if (modelLoader.GetEntityTypeByClrType(rowClr.FullName ?? rowClr.Name) is not { } rowType)
            return null;

        return FileAttribute(rowType, rowClr, parts[1]) is { } nested
            ? new Resolved(nested.Attribute, nested.Property, parent, parentProperty, rowType)
            : null;
    }

    private static (EntityAttributeDefinition Attribute, PropertyInfo Property)? FileAttribute(EntityTypeDefinition type, Type clr, string name)
    {
        var attribute = type.Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
        var property = clr.GetCachedProperty(name);
        if (attribute is null || property is null || !SparkFileDataTypes.IsFile(attribute.DataType) || FileUploadService.StoredFileType(property) is null)
            return null;
        return (attribute, property);
    }
}
