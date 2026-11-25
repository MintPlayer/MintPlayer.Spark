using System.Reflection;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Abstractions.Views;

/// <summary>
/// How a catalog enters a <see cref="SparkView{TRow}"/> (#536 D32), shared by core's view catalog and RavenDB's, which
/// enters the views no native index shadows.
/// </summary>
internal static class SparkViewEntries
{
    /// <summary>Whether <paramref name="type"/> is a concrete view declaration with a public parameterless constructor.</summary>
    public static bool IsView(Type type)
        => !type.IsAbstract && typeof(ISparkView).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null;

    /// <summary>The view declarations in <paramref name="assembly"/>, keeping what loads when some types cannot.</summary>
    public static IEnumerable<Type> ViewTypes(Assembly assembly)
    {
        Type?[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }
        return types.OfType<Type>().Where(IsView);
    }

    /// <summary>The names of the views declared in <paramref name="assemblies"/>, before any catalog is populated.</summary>
    public static IReadOnlySet<string> ViewNames(IReadOnlyList<Assembly> assemblies)
        => assemblies.SelectMany(ViewTypes).Select(t => Instantiate(t).Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static ISparkView Instantiate(Type viewType) => (ISparkView)Activator.CreateInstance(viewType)!;

    /// <summary>A view's catalog entry: its name, its collection and its row type as the projection.</summary>
    public static IndexCatalogEntry EntryFor(Type viewType)
    {
        var view = Instantiate(viewType);
        return new IndexCatalogEntry
        {
            IndexName = view.Name,
            IndexType = viewType,
            CollectionType = view.CollectionType,
            ProjectionType = view.RowType,
        };
    }

    /// <summary>The types a view's <c>[DefaultIndex]</c> claims: the collection it reads, and nothing else.</summary>
    public static IReadOnlyList<Type> ResolveClaim(IndexCatalogEntry entry)
    {
        if (entry.IndexType.GetCustomAttribute<DefaultIndexAttribute>() is not { } marker)
            return [];

        var listed = marker.EntityTypes.Distinct().ToList();
        if (listed.Count == 0 || (listed.Count == 1 && listed[0] == entry.CollectionType))
            return [entry.CollectionType!];
        throw new InvalidOperationException(
            $"[DefaultIndex({string.Join(", ", listed.Select(t => $"typeof({t.Name})"))})] on view {entry.IndexType.FullName}: a view is the " +
            $"default of the collection it reads, {entry.CollectionType!.Name}, and nothing else. Write a bare [DefaultIndex].");
    }
}
