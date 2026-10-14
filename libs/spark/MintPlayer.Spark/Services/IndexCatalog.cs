using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The application's RavenDB indexes, keyed by index name (issue #279).
/// <para>
/// Runtime resolution is declared, never ambient: a Spark query names its index (<c>indexName</c>),
/// the entity's model file names the default binding, and this catalog turns either name into the
/// CLR index and projection types. The only per-type question left is the synchronizer's —
/// "which index is this entity type's default" — answered by <c>[DefaultIndex]</c> via
/// <see cref="GetDefaultFor"/>, and validated at <see cref="Freeze"/> so runtime startup and the
/// offline model commands reject an ambiguous default identically.
/// </para>
/// </summary>
public interface IIndexCatalog
{
    /// <summary>
    /// Adds an index type, deriving its collection type from the <c>AbstractIndexCreationTask&lt;T&gt;</c>
    /// generic argument. Duplicate index names throw: two CLR types would deploy to the same RavenDB
    /// index, silently overwriting each other's definition.
    /// </summary>
    void RegisterIndex(Type indexType);

    /// <summary>Attaches a <c>[FromIndex]</c> projection type to its index's entry.</summary>
    void RegisterProjection(Type projectionType, Type indexType);

    /// <summary>
    /// Seals the catalog and elects each entity type's default index (<c>docs/default_index_PRD.md</c>,
    /// D3/D4): a type claimed by exactly one <c>[DefaultIndex]</c> has that index, with or without a view;
    /// an unclaimed type has its sole mapping index that has a view, if there is exactly one. Two claims,
    /// or several viewed indexes and no claim, are errors naming the candidates. So is a marker the
    /// catalog cannot honour: types on a single-map that are not its mapped type, a multi-map that lists
    /// none or does not derive from <c>SparkMultiMapIndexCreationTask</c>, an index whose collection
    /// Spark cannot read.
    /// </summary>
    void Freeze();

    /// <summary>The entry deployed under <paramref name="indexName"/> (case-insensitive), or <c>null</c>.</summary>
    IndexCatalogEntry? GetByIndexName(string indexName);

    /// <summary>
    /// The default index of <paramref name="entityType"/>, or <c>null</c> when it has none. It shapes the
    /// type's model file (through its view, when it has one) and is stamped on the type's minted
    /// <c>Database.*</c> queries. Synchronizer/model-shape concern only — runtime query paths resolve by
    /// name.
    /// </summary>
    IndexCatalogEntry? GetDefaultFor(Type entityType);

    /// <summary>Every entry, unordered.</summary>
    IEnumerable<IndexCatalogEntry> GetAllEntries();
}

/// <summary>One deployed index: its name, CLR type, mapped collection, optional projection, and default claims.</summary>
public sealed class IndexCatalogEntry
{
    public required string IndexName { get; init; }
    public required Type IndexType { get; init; }

    /// <summary>
    /// The mapped collection, or <see langword="null"/> when the index names none — a multi-map (which
    /// maps several and names none in its type arguments) or an otherwise opaque index.
    /// </summary>
    /// <remarks>
    /// ⚠️ Nullable on purpose. While this was <c>required Type</c>, an index with no derivable
    /// collection could not be entered in the catalog at all and was dropped, so an explicit
    /// <c>indexName</c> binding to it could never resolve. It is now registered and reachable by name,
    /// and a multi-map becomes a default only for the types its marker lists (<see cref="ClaimedTypes"/>).
    /// </remarks>
    public required Type? CollectionType { get; init; }
    public Type? ProjectionType { get; internal set; }

    /// <summary>
    /// The entity types this index's <c>[DefaultIndex]</c> marker claims: the mapped type for a bare marker
    /// on a single-map, the listed types for a multi-map, empty without a marker. Resolved and validated at
    /// freeze.
    /// </summary>
    public IReadOnlyList<Type> ClaimedTypes { get; internal set; } = [];

    /// <summary>The entity types this index was elected the default of. Computed at freeze.</summary>
    public IReadOnlyList<Type> DefaultFor { get; internal set; } = [];

    /// <summary>Whether this entry is the default index of at least one entity type. Computed at freeze.</summary>
    public bool IsDefault => DefaultFor.Count > 0;

    /// <summary>
    /// The entity types whose rows this index serves: its mapped collection type, plus every type its marker
    /// claims (a multi-map's). Startup checks that inspect "indexes over type X" iterate this, so a multi-map
    /// default is not skipped for having no single collection.
    /// </summary>
    public IEnumerable<Type> EntityTypes
        => CollectionType is null ? ClaimedTypes : ClaimedTypes.Contains(CollectionType) ? ClaimedTypes : [CollectionType, .. ClaimedTypes];

    /// <summary>
    /// Whether a query for <paramref name="entityType"/> on this index must be narrowed to that type's
    /// collection: the index is a multi-map default of the type, so its entries also hold the other mapped
    /// collections' rows. The narrowing reads the injected <see cref="SparkCollectionRow.FieldName"/> field.
    /// </summary>
    public bool FiltersByCollection(Type entityType)
        => CollectionType is null && DefaultFor.Contains(entityType);
}

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

    private void ThrowIfFrozen()
    {
        if (_frozen)
            throw new InvalidOperationException("The index catalog is frozen; registration is a startup-time concern.");
    }

}
