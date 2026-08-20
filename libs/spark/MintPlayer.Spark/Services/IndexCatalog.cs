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
/// CLR index and projection types. The only per-collection question left is the synchronizer's —
/// "which projection shapes this entity's model file" — answered by <c>[DefaultIndex]</c> via
/// <see cref="GetDefaultForCollectionType"/>, and validated at <see cref="Freeze"/> so runtime
/// startup and the offline model commands reject an ambiguous default identically.
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
    /// Seals the catalog and validates the <c>[DefaultIndex]</c> rules per collection type: zero
    /// projection-bearing indexes ⇒ no default; exactly one ⇒ implicit default; several ⇒ exactly one
    /// marked, anything else is an error naming the candidates. A marker on a projection-less index is
    /// an error too — it cannot shape a model file, so it can only be a misconfiguration.
    /// </summary>
    void Freeze();

    /// <summary>The entry deployed under <paramref name="indexName"/> (case-insensitive), or <c>null</c>.</summary>
    IndexCatalogEntry? GetByIndexName(string indexName);

    /// <summary>
    /// The entry whose projection shapes the collection type's model file, or <c>null</c> when no
    /// projection-bearing index maps it. Synchronizer/model-shape concern only — runtime query paths
    /// resolve by name.
    /// </summary>
    IndexCatalogEntry? GetDefaultForCollectionType(Type collectionType);

    /// <summary>Every entry, unordered.</summary>
    IEnumerable<IndexCatalogEntry> GetAllEntries();
}

/// <summary>One deployed index: its name, CLR type, mapped collection, and optional projection.</summary>
public sealed class IndexCatalogEntry
{
    public required string IndexName { get; init; }
    public required Type IndexType { get; init; }
    public required Type CollectionType { get; init; }
    public Type? ProjectionType { get; internal set; }

    /// <summary>Whether this entry's projection shapes its collection type's model file. Computed at freeze.</summary>
    public bool IsDefault { get; internal set; }
}

[Register(typeof(IIndexCatalog), ServiceLifetime.Singleton)]
internal partial class IndexCatalog : IIndexCatalog
{
    private readonly Dictionary<string, IndexCatalogEntry> _byIndexName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Type, IndexCatalogEntry?> _defaultByCollectionType = new();
    private readonly object _lock = new();
    private bool _frozen;

    public void RegisterIndex(Type indexType)
    {
        var collectionType = GetCollectionTypeFromIndex(indexType);
        if (collectionType == null)
        {
            Console.WriteLine($"Warning: Could not determine collection type for index {indexType.Name}");
            return;
        }

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

            foreach (var group in _byIndexName.Values.GroupBy(e => e.CollectionType))
            {
                var @default = ResolveDefault(group.Key, [.. group]);
                if (@default is not null) @default.IsDefault = true;
                _defaultByCollectionType[group.Key] = @default;
            }

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

    public IndexCatalogEntry? GetDefaultForCollectionType(Type collectionType)
    {
        lock (_lock)
        {
            if (!_frozen)
                throw new InvalidOperationException(
                    "The index catalog has not been frozen yet; default resolution runs on a validated catalog only.");
            return _defaultByCollectionType.TryGetValue(collectionType, out var entry) ? entry : null;
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
    /// The <c>[DefaultIndex]</c> rules (issue #279). Only a projection-bearing index can be the
    /// default — an index without a <c>[FromIndex]</c> companion contributes nothing to the model
    /// file, and that invariant is hash-relevant (no projection ⇒ no <c>indexName</c> in the model).
    /// </summary>
    private static IndexCatalogEntry? ResolveDefault(Type collectionType, IReadOnlyList<IndexCatalogEntry> entries)
    {
        var marked = entries.Where(HasDefaultIndexMarker).ToList();

        var markedWithoutProjection = marked.FirstOrDefault(e => e.ProjectionType is null);
        if (markedWithoutProjection is not null)
        {
            throw new InvalidOperationException(
                $"[DefaultIndex] on {markedWithoutProjection.IndexType.FullName} has no effect: the index has no " +
                $"[FromIndex] projection, so it cannot shape the model file for {collectionType.Name}. " +
                $"Remove the marker or add a projection.");
        }

        var candidates = entries.Where(e => e.ProjectionType is not null).ToList();
        if (candidates.Count <= 1) return candidates.FirstOrDefault();

        if (marked.Count != 1)
        {
            var candidateNames = string.Join(", ", candidates.Select(c => c.IndexName).OrderBy(n => n, StringComparer.Ordinal));
            var problem = marked.Count == 0
                ? "none carries [DefaultIndex]"
                : $"{marked.Count} carry [DefaultIndex] ({string.Join(", ", marked.Select(m => m.IndexName))})";
            throw new InvalidOperationException(
                $"{candidates.Count} projection-bearing indexes map collection {collectionType.Name} " +
                $"({candidateNames}) and {problem}. Exactly one index shapes the entity's model file: mark it " +
                $"with [DefaultIndex], or opt a generated index out via [GenerateIndex(IsDefault = false)].");
        }

        return marked[0];
    }

    private static bool HasDefaultIndexMarker(IndexCatalogEntry entry)
        => entry.IndexType.GetCachedCustomAttribute<DefaultIndexAttribute>() is not null;

    private void ThrowIfFrozen()
    {
        if (_frozen)
            throw new InvalidOperationException("The index catalog is frozen; registration is a startup-time concern.");
    }

    private static Type? GetCollectionTypeFromIndex(Type indexType)
    {
        return ReflectionCache.GetOrAdd<(string Op, Type Type), Type?>(
            ("IndexCatalog.IndexCollectionType", indexType),
            static k =>
            {
                var current = k.Type;
                while (current != null && current != typeof(object))
                {
                    if (current.IsGenericType)
                    {
                        var genericDef = current.GetGenericTypeDefinition();
                        if (genericDef == typeof(AbstractIndexCreationTask<>) ||
                            genericDef == typeof(AbstractMultiMapIndexCreationTask<>))
                        {
                            return current.GetGenericArguments()[0];
                        }
                    }
                    current = current.BaseType;
                }
                return null;
            });
    }
}
