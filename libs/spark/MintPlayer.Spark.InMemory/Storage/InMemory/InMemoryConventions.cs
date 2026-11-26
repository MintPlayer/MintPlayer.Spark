using System.Collections.Concurrent;
using System.Reflection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;

namespace MintPlayer.Spark.Storage.InMemory;

/// <summary>
/// Collection names and document ids, by the rules every Spark store follows: an <see cref="IHasNaturalId"/> entity
/// is stored under the id it derives, any other under <c>{Collection}/{Guid}</c> (as <c>SparkDocumentStoreConventions</c>
/// does for RavenDB). A collection is the type's plural name, one collection per concrete type, as RavenDB's default.
/// </summary>
internal static class InMemoryConventions
{
    private static readonly ConcurrentDictionary<Type, string> Collections = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> IdProperties = new();

    public static string GetCollectionName(Type type) => Collections.GetOrAdd(type, static t => Pluralize(BaseName(t)));

    /// <summary>A new id, lower case (#536 D7), as RavenDB's conventions make it.</summary>
    public static string NewId(object entity)
        => (entity is IHasNaturalId natural ? natural.GetId() : $"{GetCollectionName(entity.GetType())}/{Guid.NewGuid()}").ToLowerInvariant();

    /// <summary>The entity's <c>Id</c> property, a writable <see cref="string"/> or <see cref="Id{T}"/> one.</summary>
    public static string? ReadId(object entity) => IdProperty(entity.GetType()) is { } property
        ? SparkIdValues.ToText(property.GetValue(entity))
        : null;

    public static void WriteId(object entity, string id)
    {
        if (IdProperty(entity.GetType()) is { } property)
            property.SetValue(entity, SparkIdValues.FromText(property.PropertyType, id));
    }

    private static PropertyInfo? IdProperty(Type type) => IdProperties.GetOrAdd(type, static t =>
        t.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance) is { PropertyType: var p, CanWrite: true } property
            && (p == typeof(string) || SparkIdValues.IsId(p))
            ? property
            : null);

    private static string BaseName(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }

    /// <summary>English plurals for type names: enough for entity names, not a general inflector.</summary>
    internal static string Pluralize(string name)
    {
        if (name.Length == 0)
            return name;
        if (name.EndsWith("Person", StringComparison.Ordinal))
            return name[..^"Person".Length] + "People";
        if (name.EndsWith("person", StringComparison.Ordinal))
            return name[..^"person".Length] + "people";
        if (name.EndsWith('y') && name.Length > 1 && !"aeiouAEIOU".Contains(name[^2]))
            return name[..^1] + "ies";
        if (name.EndsWith('s') || name.EndsWith('x') || name.EndsWith('z') || name.EndsWith("ch", StringComparison.Ordinal)
            || name.EndsWith("sh", StringComparison.Ordinal))
            return name + "es";
        return name + "s";
    }
}
