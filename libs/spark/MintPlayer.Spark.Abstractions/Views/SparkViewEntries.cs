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

    /// <summary>
    /// Whether the view yields one row per entity of its collection: no reduce, one map (no <c>Concat</c>), and no
    /// fan-out (no <c>SelectMany</c>, which yields a row per list item, #536 D48).
    /// </summary>
    public static bool IsRowPerEntity(ISparkView view)
    {
        if (view.ReduceDefinition is not null) return false;

        // The map is a chain of Queryable calls down to s.Set<T>(): Concat at its top, SelectMany anywhere in it.
        for (var node = view.Definition.Body as System.Linq.Expressions.MethodCallExpression;
             node is { Method.DeclaringType: var declaring } && declaring == typeof(Queryable);
             node = node.Arguments[0] as System.Linq.Expressions.MethodCallExpression)
        {
            if (node.Method.Name is nameof(Queryable.Concat) or nameof(Queryable.SelectMany)) return false;
        }
        return true;
    }

    /// <summary>A view's catalog entry: its name, its collection and its row type as the projection.</summary>
    public static IndexCatalogEntry EntryFor(Type viewType)
    {
        var view = Instantiate(viewType);
        return new IndexCatalogEntry
        {
            IndexName = view.Name,
            IndexType = viewType,
            // S16: a reduce, multi-map or fan-out view has no row per entity of one collection, so it maps none and
            // can never be an entity type's default (as a native multi-map without a marker). One that has is a
            // default only when it claims [DefaultIndex] (D48): see the catalogs' elections.
            CollectionType = IsRowPerEntity(view) ? view.CollectionType : null,
            ProjectionType = view.RowType,
        };
    }

    /// <summary>The types a view's <c>[DefaultIndex]</c> claims: the collection it reads, and nothing else.</summary>
    public static IReadOnlyList<Type> ResolveClaim(IndexCatalogEntry entry)
    {
        if (entry.IndexType.GetCustomAttribute<DefaultIndexAttribute>() is not { } marker)
            return [];

        if (entry.CollectionType is null)
            throw new InvalidOperationException(
                $"[DefaultIndex] on view {entry.IndexType.FullName}: a view with a Reduce or several maps (Concat) has no row per entity, so it cannot be an entity type's default.");

        var listed = marker.EntityTypes.Distinct().ToList();
        if (listed.Count == 0 || (listed.Count == 1 && listed[0] == entry.CollectionType))
            return [entry.CollectionType!];
        throw new InvalidOperationException(
            $"[DefaultIndex({string.Join(", ", listed.Select(t => $"typeof({t.Name})"))})] on view {entry.IndexType.FullName}: a view is the " +
            $"default of the collection it reads, {entry.CollectionType!.Name}, and nothing else. Write a bare [DefaultIndex].");
    }
}
