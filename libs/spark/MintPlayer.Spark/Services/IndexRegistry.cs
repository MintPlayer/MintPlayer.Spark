using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Reflection;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Registry that tracks the relationship between RavenDB indexes, collection types, and projection types.
/// </summary>
public interface IIndexRegistry
{
    /// <summary>
    /// Registers an index type, extracting its collection type from the generic parameter.
    /// </summary>
    void RegisterIndex(Type indexType);

    /// <summary>
    /// Registers a projection type that is produced by an index.
    /// </summary>
    void RegisterProjection(Type projectionType, Type indexType);

    /// <summary>
    /// Gets the default registration for a collection type, if any index maps from it — the one
    /// the generic query path (grids, model synchronization, model hashing) resolves through.
    /// When several indexes map the same collection, the default is the registration with the
    /// smallest index name under ordinal comparison; the others remain fully usable via
    /// <see cref="GetRegistrationsForCollectionType"/> and by-name lookups, or directly through
    /// <c>session.Query&lt;TProjection, TIndex&gt;()</c>, which never consults the registry.
    /// Name order (rather than registration order) keeps the winner stable across recompiles:
    /// registration order tracks type-metadata order, so reordering two index classes in a file
    /// would silently move the model hash.
    /// </summary>
    IndexRegistration? GetRegistrationForCollectionType(Type collectionType);

    /// <summary>
    /// Gets all registrations whose index maps the given collection type, default first.
    /// </summary>
    IReadOnlyList<IndexRegistration> GetRegistrationsForCollectionType(Type collectionType);

    /// <summary>
    /// Gets the registration by index name.
    /// </summary>
    IndexRegistration? GetRegistrationByIndexName(string indexName);

    /// <summary>
    /// Gets all registered indexes.
    /// </summary>
    IEnumerable<IndexRegistration> GetAllRegistrations();

    /// <summary>
    /// Checks whether the given type is a projection type for any registered index.
    /// </summary>
    bool IsProjectionType(Type type);
}

/// <summary>
/// Represents a registered index with its associated types.
/// </summary>
public sealed class IndexRegistration
{
    public required string IndexName { get; init; }
    public required Type IndexType { get; init; }
    public required Type CollectionType { get; init; }
    public Type? ProjectionType { get; set; }
}

[Register(typeof(IIndexRegistry), ServiceLifetime.Singleton)]
internal partial class IndexRegistry : IIndexRegistry
{
    private readonly Dictionary<string, IndexRegistration> _byIndexName = new(StringComparer.OrdinalIgnoreCase);
    // Every registration for a collection type is retained, kept sorted by ordinal index name so
    // the default (element 0) is deterministic across scan orders, assembly orders and recompiles.
    private readonly Dictionary<Type, List<IndexRegistration>> _byCollectionType = new();
    private readonly object _lock = new();

    public void RegisterIndex(Type indexType)
    {
        var collectionType = GetCollectionTypeFromIndex(indexType);
        if (collectionType == null)
        {
            Console.WriteLine($"Warning: Could not determine collection type for index {indexType.Name}");
            return;
        }

        var indexName = GetIndexName(indexType);

        lock (_lock)
        {
            if (_byIndexName.ContainsKey(indexName))
            {
                // Already registered, skip
                return;
            }

            var registration = new IndexRegistration
            {
                IndexName = indexName,
                IndexType = indexType,
                CollectionType = collectionType
            };

            _byIndexName[indexName] = registration;

            if (!_byCollectionType.TryGetValue(collectionType, out var registrations))
            {
                _byCollectionType[collectionType] = registrations = [];
            }
            var insertAt = registrations.FindIndex(r => string.CompareOrdinal(indexName, r.IndexName) < 0);
            registrations.Insert(insertAt < 0 ? registrations.Count : insertAt, registration);

            if (registrations.Count > 1)
            {
                Console.WriteLine(
                    $"Warning: {registrations.Count} indexes map collection {collectionType.Name} " +
                    $"({string.Join(", ", registrations.Select(r => r.IndexName))}). " +
                    $"The generic query path uses {registrations[0].IndexName}; the others remain " +
                    $"usable via session.Query<TProjection, TIndex>().");
            }

            Console.WriteLine($"Registered index: {indexName} (Collection: {collectionType.Name})");
        }
    }

    public void RegisterProjection(Type projectionType, Type indexType)
    {
        var indexName = GetIndexName(indexType);

        lock (_lock)
        {
            if (_byIndexName.TryGetValue(indexName, out var registration))
            {
                registration.ProjectionType = projectionType;
                Console.WriteLine($"Registered projection: {projectionType.Name} for index {indexName}");
            }
            else
            {
                Console.WriteLine($"Warning: Cannot register projection {projectionType.Name} - index {indexName} not found");
            }
        }
    }

    public IndexRegistration? GetRegistrationForCollectionType(Type collectionType)
    {
        lock (_lock)
        {
            return _byCollectionType.TryGetValue(collectionType, out var registrations) ? registrations[0] : null;
        }
    }

    public IReadOnlyList<IndexRegistration> GetRegistrationsForCollectionType(Type collectionType)
    {
        lock (_lock)
        {
            return _byCollectionType.TryGetValue(collectionType, out var registrations)
                ? [.. registrations]
                : [];
        }
    }

    public IndexRegistration? GetRegistrationByIndexName(string indexName)
    {
        lock (_lock)
        {
            return _byIndexName.TryGetValue(indexName, out var registration) ? registration : null;
        }
    }

    public IEnumerable<IndexRegistration> GetAllRegistrations()
    {
        lock (_lock)
        {
            return _byIndexName.Values.ToList();
        }
    }

    public bool IsProjectionType(Type type)
    {
        lock (_lock)
        {
            // Scan every registration, not just defaults: a projection for a non-default index
            // is still a projection — mistaking it for an entity would emit it as its own model file.
            return _byIndexName.Values.Any(r => r.ProjectionType == type);
        }
    }

    private static Type? GetCollectionTypeFromIndex(Type indexType)
    {
        return ReflectionCache.GetOrAdd<(string Op, Type Type), Type?>(
            ("IndexRegistry.IndexCollectionType", indexType),
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

    private static string GetIndexName(Type indexType)
    {
        // RavenDB uses the class name as the index name
        return indexType.Name;
    }
}
