using System.Reflection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Services;

/// <summary>The RavenDB implementation of <see cref="IIndexCatalog"/>: it classifies index types (#551 D20).</summary>
[Register(typeof(IIndexCatalog), ServiceLifetime.Singleton)]
internal partial class IndexCatalog : IIndexCatalog
{
    private readonly Dictionary<string, IndexCatalogEntry> _byIndexName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Type, IndexCatalogEntry> _defaultByEntityType = new();
    private readonly object _lock = new();
    private bool _frozen;

    public void RegisterIndex(Type indexType)
    {
        // A multi-map or otherwise opaque index still gets an entry, with a null collection. Dropping
        // it here is what made an explicit "indexName" binding to such an index unresolvable while
        // RavenDB had deployed it perfectly happily.
        var collectionType = RavenIndexHierarchy.MappedCollection(indexType);

        lock (_lock)
        {
            ThrowIfFrozen();

            var indexName = indexType.Name;
            if (_byIndexName.TryGetValue(indexName, out var existing))
            {
                // The same type surfacing through two module registrations is idempotent; a different
                // type under the same name would deploy over the first index's definition.
                if (existing.IndexType == indexType) return;
                throw new InvalidOperationException(
                    $"Index name '{indexName}' is declared by both {existing.IndexType.FullName} and " +
                    $"{indexType.FullName}. RavenDB identifies an index by its class name, so one would " +
                    $"silently overwrite the other. Rename one of them.");
            }

            _byIndexName[indexName] = new IndexCatalogEntry
            {
                IndexName = indexName,
                IndexType = indexType,
                CollectionType = collectionType,
            };
        }
    }

    public void RegisterProjection(Type projectionType, Type indexType)
    {
        lock (_lock)
        {
            ThrowIfFrozen();

            if (_byIndexName.TryGetValue(indexType.Name, out var entry))
            {
                entry.ProjectionType = projectionType;
            }
            else
            {
                Console.WriteLine(
                    $"Warning: Cannot register projection {projectionType.Name} - index {indexType.Name} not found");
            }
        }
    }

    public void Freeze()
    {
        lock (_lock)
        {
            if (_frozen) return;

            var entries = _byIndexName.Values.OrderBy(e => e.IndexName, StringComparer.Ordinal).ToList();
            foreach (var entry in entries)
                entry.ClaimedTypes = ResolveClaims(entry);

            // Every type that could have a default: each single-map's mapped type, and each claimed type
            // (which is how a multi-map enters the election — it names no collection of its own).
            var entityTypes = entries
                .Select(e => e.CollectionType)
                .OfType<Type>()
                .Concat(entries.SelectMany(e => e.ClaimedTypes))
                .Distinct();

            var defaultFor = new Dictionary<IndexCatalogEntry, List<Type>>();
            foreach (var entityType in entityTypes)
            {
                var @default = Elect(entityType, entries);
                if (@default is null) continue;

                _defaultByEntityType[entityType] = @default;
                if (!defaultFor.TryGetValue(@default, out var types)) defaultFor[@default] = types = [];
                types.Add(entityType);
            }

            foreach (var (entry, types) in defaultFor)
                entry.DefaultFor = types;

            _frozen = true;
        }
    }

    public IndexCatalogEntry? GetByIndexName(string indexName)
    {
        lock (_lock)
        {
            return _byIndexName.TryGetValue(indexName, out var entry) ? entry : null;
        }
    }

    public IndexCatalogEntry? GetDefaultFor(Type entityType)
    {
        lock (_lock)
        {
            if (!_frozen)
                throw new InvalidOperationException(
                    "The index catalog has not been frozen yet; default resolution runs on a validated catalog only.");
            return _defaultByEntityType.TryGetValue(entityType, out var entry) ? entry : null;
        }
    }

    public IEnumerable<IndexCatalogEntry> GetAllEntries()
    {
        lock (_lock)
        {
            return _byIndexName.Values.ToList();
        }
    }

    /// <summary>
    /// The election for one entity type (D3). A claim wins, view or not — a viewless default shapes nothing,
    /// and its queries return entity documents (SP4). Without a claim, only an index <em>with a view</em> is a
    /// default implicitly, and only when it is the sole one: an index without a view contributes nothing to the
    /// model file, so electing one silently would stamp an <c>indexName</c> nobody chose.
    /// </summary>
    private static IndexCatalogEntry? Elect(Type entityType, IReadOnlyList<IndexCatalogEntry> entries)
    {
        var claims = entries.Where(e => e.ClaimedTypes.Contains(entityType)).ToList();
        if (claims.Count == 1) return claims[0];
        if (claims.Count > 1)
        {
            throw new InvalidOperationException(
                $"{claims.Count} indexes claim [DefaultIndex] for entity type {entityType.Name} " +
                $"({string.Join(", ", claims.Select(c => c.IndexName))}). An entity type has exactly one default " +
                $"index: remove the marker from all but one of them, or drop {entityType.Name} from a multi-map's " +
                $"[DefaultIndex(...)] list.");
        }

        var viewed = entries.Where(e => e.CollectionType == entityType && e.ProjectionType is not null).ToList();
        if (viewed.Count <= 1) return viewed.FirstOrDefault();

        throw new InvalidOperationException(
            $"{viewed.Count} indexes with a [FromIndex] view map entity type {entityType.Name} " +
            $"({string.Join(", ", viewed.Select(c => c.IndexName))}) and none claims it with [DefaultIndex]. " +
            $"Exactly one index is an entity type's default: mark it with [DefaultIndex].");
    }

    /// <summary>
    /// The entity types an entry's <c>[DefaultIndex]</c> marker claims (D1), or an error for a marker the
    /// catalog cannot honour. The analyzers report the same mistakes at compile time for same-project
    /// indexes; this is the check that also sees indexes from other assemblies and cannot be switched off.
    /// </summary>
    private static IReadOnlyList<Type> ResolveClaims(IndexCatalogEntry entry)
    {
        var marker = entry.IndexType.GetCachedCustomAttribute<DefaultIndexAttribute>();
        if (marker is null) return [];

        var listed = marker.EntityTypes.Distinct().ToList();
        var kind = RavenIndexHierarchy.Classify(entry.IndexType, out var mapped);
        switch (kind)
        {
            case RavenIndexKind.Map:
                if (listed.Count == 0 || (listed.Count == 1 && listed[0] == mapped)) return [mapped!];
                throw new InvalidOperationException(
                    $"[DefaultIndex({string.Join(", ", listed.Select(t => $"typeof({t.Name})"))})] on " +
                    $"{entry.IndexType.FullName}: a single-map index is the default of the type it maps, " +
                    $"{mapped!.Name}, and nothing else. Write a bare [DefaultIndex] (or [DefaultIndex(typeof({mapped.Name}))]).");

            case RavenIndexKind.MultiMap when listed.Count == 0:
                throw new InvalidOperationException(
                    $"[DefaultIndex] on multi-map index {entry.IndexType.FullName} lists no entity type. A multi-map " +
                    $"maps several collections, so its marker names the types it is the default of: " +
                    $"[DefaultIndex(typeof(A), typeof(B))].");

            case RavenIndexKind.MultiMap when !DerivesFromSparkMultiMap(entry.IndexType):
                throw new InvalidOperationException(
                    $"Multi-map index {entry.IndexType.FullName} claims [DefaultIndex] but does not derive from " +
                    $"SparkMultiMapIndexCreationTask<T>. Only that base adds the collection discriminator a multi-map " +
                    $"default needs; without it a query for one mapped type returns the other types' rows as that " +
                    $"type, outside their row policies. Change the base class.");

            case RavenIndexKind.MultiMap:
                return listed;

            default:
                throw new InvalidOperationException(
                    $"[DefaultIndex] on {entry.IndexType.FullName}: Spark cannot tell which collections this index " +
                    $"maps, so it cannot be a default. A JavaScript index is never a default index; derive from " +
                    $"SparkIndexCreationTask<T> (one type) or SparkMultiMapIndexCreationTask<T> (several types) instead.");
        }
    }

    private static bool DerivesFromSparkMultiMap(Type indexType)
    {
        for (var current = indexType; current is not null && current != typeof(object); current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(SparkMultiMapIndexCreationTask<>))
                return true;
        }
        return false;
    }

    public void RegisterAssemblies(IReadOnlyList<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            var indexTypes = ReflectionCache.GetOrAdd<(string Op, Assembly Asm), IReadOnlyList<Type>>(
                ("IndexCatalog.IndexTypes", assembly),
                static k => LoadableTypes(k.Asm).Where(t => !t.IsAbstract && RavenIndexHierarchy.IsIndex(t)).ToArray());
            foreach (var indexType in indexTypes)
                RegisterIndex(indexType);
        }

        foreach (var assembly in assemblies)
        {
            var projectionTypes = ReflectionCache.GetOrAdd<(string Op, Assembly Asm), IReadOnlyList<Type>>(
                ("IndexCatalog.ProjectionTypes", assembly),
                static k => LoadableTypes(k.Asm).Where(t => t.GetCachedCustomAttribute<FromIndexAttribute>() != null).ToArray());
            foreach (var projectionType in projectionTypes)
                RegisterProjection(projectionType, projectionType.GetCachedCustomAttribute<FromIndexAttribute>()!.IndexType);
        }
    }

    /// <summary>
    /// The types of an assembly, keeping what loaded when some types cannot: an optional peer dependency that is absent
    /// is a deployment fact, not a Spark defect. Inside the cache factory, because <c>ReflectionCache</c> re-throws a
    /// throwing factory for the life of the process.
    /// </summary>
    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            var loaded = ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
            Console.WriteLine(
                $"Warning: {assembly.GetName().Name} has {ex.Types.Length - loaded.Length} type(s) that could not be " +
                $"loaded; scanning the {loaded.Length} that did. First loader error: {ex.LoaderExceptions.FirstOrDefault()?.Message}");
            return loaded;
        }
    }

    /// <remarks>
    /// Every map's fields, not only the map over this type's collection: offline there is no document store to name
    /// collections with, and a field another map emits is indexed, so sorting on it is no error.
    /// </remarks>
    public void BuildDefinition(IndexCatalogEntry entry)
        => (Activator.CreateInstance(entry.IndexType) as AbstractIndexCreationTask)?.CreateIndexDefinition();

    public IReadOnlySet<string>? GetQuerySurface(IndexCatalogEntry entry)
    {
        IndexDefinition definition;
        try
        {
            if (Activator.CreateInstance(entry.IndexType) is not IAbstractIndexCreationTask task) return null;
            definition = task.CreateIndexDefinition();
        }
        catch (Exception ex) when (ex is MissingMethodException or NotSupportedException or TargetInvocationException or InvalidOperationException)
        {
            Console.WriteLine($"Warning: could not read the maps of viewless default index '{entry.IndexName}' ({ex.Message}); " +
                              "its attributes keep the default sort and filter capabilities.");
            return null;
        }

        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var map in definition.Maps)
        {
            if (SparkMapScanner.EmittedFields(map) is not { } emitted)
            {
                Console.WriteLine($"Warning: could not read the fields emitted by a map of viewless default index '{entry.IndexName}'; " +
                                  "its attributes keep the default sort and filter capabilities.");
                return null;
            }
            fields.UnionWith(emitted);
        }
        return fields;
    }

    private void ThrowIfFrozen()
    {
        if (_frozen)
            throw new InvalidOperationException("The index catalog is frozen; registration is a startup-time concern.");
    }

}
