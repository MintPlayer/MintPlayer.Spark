using System.Collections;
using System.Reflection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Abstractions.Views;

namespace MintPlayer.Spark.Relational.Model;

/// <summary>How a table's key column holds the entity's id (#536 D23 rule 8).</summary>
public enum SparkKeyShape
{
    /// <summary>A <see cref="string"/> <c>Id</c> property: the whole id, as text.</summary>
    Text,

    /// <summary>A Guid-kind <see cref="Id{T}"/>: the key as <c>uniqueidentifier</c>/<c>uuid</c>.</summary>
    Guid,

    /// <summary>An Integer-kind <see cref="Id{T}"/>: the key as <c>bigint</c>.</summary>
    Integer,

    /// <summary>A Natural-kind <see cref="Id{T}"/>: the key (everything after the collection) as text.</summary>
    Natural,
}

/// <summary>One collection's table: the root type, its stored derived types (TPH) and its key.</summary>
public sealed class SparkTable
{
    internal SparkTable(Type root, string name, SparkKeyShape key)
    {
        Root = root;
        Name = name;
        Key = key;
        Prefix = name.ToLowerInvariant() + "/";
        IdProperty = root.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)!;
    }

    public Type Root { get; }

    /// <summary>The table name, which is the collection name.</summary>
    public string Name { get; }

    public SparkKeyShape Key { get; }

    /// <summary>The lowercase id prefix of a typed key, <c>"people/"</c> (D7).</summary>
    public string Prefix { get; }

    public PropertyInfo IdProperty { get; }

    public List<Type> Derived { get; } = [];

    /// <summary>
    /// The value the <c>Id</c> property holds for <paramref name="id"/>, or <see langword="null"/> when the text is not an
    /// id of this table: another collection's, or a key the column can't hold. Ids are lowered first (D7).
    /// </summary>
    public object? KeyValue(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        var lowered = id.ToLowerInvariant();
        if (Key == SparkKeyShape.Text)
            return lowered;
        if (!lowered.StartsWith(Prefix, StringComparison.Ordinal) || lowered.Length == Prefix.Length)
            return null;
        var key = lowered[Prefix.Length..];
        var valid = Key switch
        {
            SparkKeyShape.Guid => System.Guid.TryParse(key, out _),
            SparkKeyShape.Integer => long.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _),
            _ => true,
        };
        return valid ? SparkIdValues.FromText(IdProperty.PropertyType, lowered) : null;
    }
}

/// <summary>A reference list (<c>List&lt;Id&lt;T&gt;&gt;</c>, or a <c>[Reference]</c> string list) as a join table (D26).</summary>
public sealed record SparkJoinTable(Type Owner, PropertyInfo Property, Type ElementType, SparkTable? Target, string Name);

/// <summary>
/// The relational shape of the stored types (#536 D1, D25, D26, D36): one table per collection, a TPH table for a
/// collection with stored derived types, join tables for reference lists. The EF model is built from it
/// (<see cref="SparkDbContext"/>).
/// </summary>
public sealed class SparkRelationalModel
{
    private readonly Dictionary<Type, SparkTable> byType = [];

    private SparkRelationalModel() { }

    public IReadOnlyList<SparkTable> Tables { get; private set; } = [];

    public IReadOnlyList<SparkJoinTable> Joins { get; private set; } = [];

    /// <summary>The neutral views (D32) the context maps to SQL views.</summary>
    public IReadOnlyList<ISparkView> Views { get; private set; } = [];

    /// <param name="storedTypes">The stored types (<see cref="ISparkStoredTypes"/>).</param>
    /// <param name="kindOf">The id kind of a type with an <see cref="Id{T}"/> key (<see cref="ISparkIds.KindOf"/>'s rule).</param>
    /// <param name="views">The neutral views to map.</param>
    public static SparkRelationalModel Build(IEnumerable<Type> storedTypes, Func<Type, SparkIdKind> kindOf, IEnumerable<ISparkView>? views = null)
    {
        var model = new SparkRelationalModel();
        var types = storedTypes.Distinct().ToList();
        var tables = new List<SparkTable>();

        foreach (var type in types.Where(t => !types.Any(other => other != t && other.IsAssignableFrom(t))).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var idProperty = type.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"Stored type {type.FullName} has no public Id property.");
            var shape = idProperty.PropertyType == typeof(string)
                ? SparkKeyShape.Text
                : SparkIdValues.IsId(idProperty.PropertyType)
                    ? kindOf(type) switch
                    {
                        SparkIdKind.Integer => SparkKeyShape.Integer,
                        SparkIdKind.Natural => SparkKeyShape.Natural,
                        _ => SparkKeyShape.Guid,
                    }
                    : throw new InvalidOperationException($"Stored type {type.FullName}: Id must be a string or an Id<{type.Name}>.");
            var table = new SparkTable(type, SparkCollectionNames.Of(type), shape);
            tables.Add(table);
            model.byType[type] = table;
        }

        // L1 (D7): two collections whose names are equal ignoring case are one table on SQL Server and one collection
        // on RavenDB.
        foreach (var clash in tables.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            throw new InvalidOperationException(
                $"Stored types {string.Join(" and ", clash.Select(t => t.Root.FullName))} map to the same collection '{clash.Key}'. Rename one of them.");

        foreach (var type in types.Where(t => !model.byType.ContainsKey(t)))
        {
            var table = tables.Where(t => t.Root.IsAssignableFrom(type)).OrderByDescending(t => Depth(t.Root)).First();
            table.Derived.Add(type);
            model.byType[type] = table;
        }

        model.Tables = tables;
        model.Joins = [.. types.SelectMany(t => JoinsOf(model, t))];
        model.Views = [.. views ?? []];
        return model;
    }

    private static int Depth(Type type)
    {
        var depth = 0;
        for (var t = type; t is not null; t = t.BaseType) depth++;
        return depth;
    }

    private static IEnumerable<SparkJoinTable> JoinsOf(SparkRelationalModel model, Type type)
    {
        foreach (var property in DeclaredStoredProperties(type))
        {
            if (ReferenceListElement(property) is not { } element)
                continue;
            var target = SparkIdValues.TargetOf(element) ?? property.GetCustomAttribute<ReferenceAttribute>()?.TargetType;
            yield return new SparkJoinTable(type, property, element, target is null ? null : model.TableOf(target),
                model.TableOf(type)!.Name + property.Name);
        }
    }

    /// <summary>The element type of a reference list: <c>List&lt;Id&lt;T&gt;&gt;</c>, or a string list marked <c>[Reference]</c>.</summary>
    internal static Type? ReferenceListElement(PropertyInfo property)
    {
        if (CollectionElement(property.PropertyType) is not { } element)
            return null;
        if (SparkIdValues.IsId(element))
            return element;
        return element == typeof(string) && property.GetCustomAttribute<ReferenceAttribute>() is not null ? element : null;
    }

    /// <summary>The element type of a list, array or other collection type; <see langword="null"/> for a string or a non-collection.</summary>
    internal static Type? CollectionElement(Type type)
    {
        if (type == typeof(string))
            return null;
        if (type.IsArray)
            return type.GetElementType();
        if (type.IsGenericType && type.GetGenericArguments().Length == 1 && typeof(IEnumerable).IsAssignableFrom(type))
            return type.GetGenericArguments()[0];
        return null;
    }

    /// <summary>The public instance properties a type declares itself, which TPH maps on the type that declares them.</summary>
    internal static IEnumerable<PropertyInfo> DeclaredStoredProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.GetIndexParameters().Length == 0);

    /// <summary>The table a stored type (or a stored derived type) lives in; <see langword="null"/> when it isn't stored.</summary>
    public SparkTable? TableOf(Type type)
    {
        if (byType.TryGetValue(type, out var table))
            return table;
        return Tables.Where(t => t.Root.IsAssignableFrom(type)).OrderByDescending(t => Depth(t.Root)).FirstOrDefault();
    }

    public SparkTable RequireTable(Type type)
        => TableOf(type) ?? throw new InvalidOperationException(
            $"{type.FullName} is not a stored type. List it in the Spark model, or add it with services.AddSparkStoredType<{type.Name}>().");
}
