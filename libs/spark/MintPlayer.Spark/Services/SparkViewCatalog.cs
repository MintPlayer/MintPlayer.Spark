using System.Reflection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Views;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The catalog of a store without native indexes (#551 D20, #536 D32): it knows the <see cref="SparkView{TRow}"/>
/// declarations, which every provider renders, and nothing else. A provider with native indexes registers its own
/// catalog after core's, and that registration wins.
/// </summary>
/// <remarks>
/// A model that names something other than a view on a provider without indexes is refused at startup by
/// <see cref="SparkIndexBindingStartupCheck"/> (run by <c>UseSpark()</c>), not here: <see cref="RegisterIndex"/> throws
/// only for a caller that hands it a native index type directly.
/// </remarks>
[Register(typeof(IIndexCatalog), ServiceLifetime.Singleton)]
internal sealed partial class SparkViewCatalog : IIndexCatalog
{
    private readonly Dictionary<string, IndexCatalogEntry> byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Type, IndexCatalogEntry> defaults = [];
    private readonly Lock gate = new();
    private bool frozen;

    public void RegisterIndex(Type indexType)
    {
        if (!IsView(indexType))
            throw new NotSupportedException($"This store provider has no native indexes, so index {indexType.Name} cannot be registered. Declare it as a SparkView<TRow>.");

        var entry = EntryFor(indexType);
        lock (gate)
        {
            if (frozen)
                throw new InvalidOperationException("The index catalog is frozen; registration is a startup-time concern.");
            if (byName.TryGetValue(entry.IndexName, out var existing))
            {
                if (existing.IndexType == indexType) return;
                throw new InvalidOperationException(
                    $"View name '{entry.IndexName}' is declared by both {existing.IndexType.FullName} and {indexType.FullName}. Rename one of them.");
            }

            byName[entry.IndexName] = entry;
        }
    }

    public void RegisterProjection(Type projectionType, Type indexType)
        => throw new NotSupportedException(
            $"This store provider has no native indexes, so [FromIndex] view {projectionType.Name} cannot be registered. A SparkView<TRow> names its row type itself.");

    public void RegisterAssemblies(IReadOnlyList<Assembly> assemblies)
    {
        foreach (var type in assemblies.SelectMany(ViewTypes))
            RegisterIndex(type);
    }

    public void Freeze()
    {
        lock (gate)
        {
            if (frozen) return;

            foreach (var entry in byName.Values)
                entry.ClaimedTypes = entry.IndexType.GetCustomAttribute<DefaultIndexAttribute>() is { } marker
                    ? ResolveClaim(entry, marker)
                    : [];

            var byDefault = new Dictionary<IndexCatalogEntry, List<Type>>();
            foreach (var entityType in byName.Values.Select(e => e.CollectionType!).Distinct())
            {
                if (Elect(entityType, [.. byName.Values.OrderBy(e => e.IndexName, StringComparer.Ordinal)]) is not { } @default)
                    continue;
                defaults[entityType] = @default;
                if (!byDefault.TryGetValue(@default, out var types)) byDefault[@default] = types = [];
                types.Add(entityType);
            }

            foreach (var (entry, types) in byDefault)
                entry.DefaultFor = types;

            frozen = true;
        }
    }

    public IndexCatalogEntry? GetByIndexName(string indexName)
    {
        lock (gate)
            return byName.GetValueOrDefault(indexName);
    }

    public IndexCatalogEntry? GetDefaultFor(Type entityType)
    {
        lock (gate)
        {
            if (!frozen)
                throw new InvalidOperationException("The index catalog has not been frozen yet; default resolution runs on a validated catalog only.");
            return defaults.GetValueOrDefault(entityType);
        }
    }

    public IEnumerable<IndexCatalogEntry> GetAllEntries()
    {
        lock (gate)
            return [.. byName.Values];
    }

    /// <summary>A view always has its row type, so there is no viewless surface to read.</summary>
    public IReadOnlySet<string>? GetQuerySurface(IndexCatalogEntry entry) => null;

    /// <summary>Reads the declaration, as the providers will: a view that starts from no <c>Set&lt;T&gt;()</c> throws here.</summary>
    public void BuildDefinition(IndexCatalogEntry entry) => _ = Instantiate(entry.IndexType).CollectionType;

    /// <summary>Whether <paramref name="type"/> is a concrete view declaration.</summary>
    public static bool IsView(Type type)
        => !type.IsAbstract && typeof(ISparkView).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null;

    /// <summary>The view declarations in <paramref name="assembly"/>.</summary>
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

    /// <summary>A view is the default of the collection it enumerates, and nothing else.</summary>
    private static IReadOnlyList<Type> ResolveClaim(IndexCatalogEntry entry, DefaultIndexAttribute marker)
    {
        var listed = marker.EntityTypes.Distinct().ToList();
        if (listed.Count == 0 || (listed.Count == 1 && listed[0] == entry.CollectionType))
            return [entry.CollectionType!];
        throw new InvalidOperationException(
            $"[DefaultIndex({string.Join(", ", listed.Select(t => $"typeof({t.Name})"))})] on view {entry.IndexType.FullName}: a view is the " +
            $"default of the collection it reads, {entry.CollectionType!.Name}, and nothing else. Write a bare [DefaultIndex].");
    }

    /// <summary>The same election as the RavenDB catalog's (default_index_PRD D3): a claim wins, else the sole view.</summary>
    private static IndexCatalogEntry? Elect(Type entityType, IReadOnlyList<IndexCatalogEntry> entries)
    {
        var claims = entries.Where(e => e.ClaimedTypes.Contains(entityType)).ToList();
        if (claims.Count == 1) return claims[0];
        if (claims.Count > 1)
            throw new InvalidOperationException(
                $"{claims.Count} views claim [DefaultIndex] for entity type {entityType.Name} ({string.Join(", ", claims.Select(c => c.IndexName))}). " +
                "An entity type has exactly one default: remove the marker from all but one of them.");

        var viewed = entries.Where(e => e.CollectionType == entityType).ToList();
        if (viewed.Count <= 1) return viewed.FirstOrDefault();
        throw new InvalidOperationException(
            $"{viewed.Count} views read entity type {entityType.Name} ({string.Join(", ", viewed.Select(c => c.IndexName))}) and none claims it " +
            "with [DefaultIndex]. Exactly one is an entity type's default: mark it with [DefaultIndex].");
    }
}
