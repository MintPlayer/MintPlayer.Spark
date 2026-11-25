using System.Collections.Concurrent;

namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// The collection name of a type for a store that has no naming conventions of its own (InMemory, the relational
/// providers): the type's plural name, one collection per concrete type, as RavenDB's default. RavenDB keeps its own
/// conventions.
/// </summary>
public static class SparkCollectionNames
{
    private static readonly ConcurrentDictionary<Type, string> Names = new();

    public static string Of(Type type) => Names.GetOrAdd(type, static t => Pluralize(BaseName(t)));

    private static string BaseName(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }

    /// <summary>English plurals for type names: enough for entity names, not a general inflector.</summary>
    public static string Pluralize(string name)
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
