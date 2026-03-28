using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Registry that tracks the relationship between indexes, collection types, and projection types.
/// Storage-agnostic: providers register indexes during initialization.
/// </summary>
public interface IIndexRegistry
{
    /// <summary>
    /// Registers an index type, attempting to extract its collection type from the generic parameter.
    /// Falls back to provider-specific inspection if needed.
    /// </summary>
    void RegisterIndex(Type indexType);

    /// <summary>
    /// Registers an index with pre-resolved metadata.
    /// Used by storage providers to register indexes without requiring provider-specific type inspection in core.
    /// </summary>
    void RegisterIndex(string indexName, Type collectionType, Type indexType);

    /// <summary>
    /// Registers a projection type that is produced by an index.
    /// </summary>
    void RegisterProjection(Type projectionType, Type indexType);

    /// <summary>
    /// Gets the registration for a collection type, if any index maps from it.
    /// </summary>
    IndexRegistration? GetRegistrationForCollectionType(Type collectionType);

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
    private readonly Dictionary<Type, IndexRegistration> _byCollectionType = new();
    private readonly object _lock = new();

    public void RegisterIndex(Type indexType)
    {
        // Try to extract the collection type from the first generic argument of the base class
        var collectionType = GetGenericArgumentFromBaseClass(indexType);
        if (collectionType == null)
        {
            Console.WriteLine($"Warning: Could not determine collection type for index {indexType.Name}");
            return;
        }

        var indexName = indexType.Name;
        RegisterIndex(indexName, collectionType, indexType);
    }

    public void RegisterIndex(string indexName, Type collectionType, Type indexType)
    {
        lock (_lock)
        {
            if (_byIndexName.TryGetValue(indexName, out var existing))
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
            _byCollectionType[collectionType] = registration;

            Console.WriteLine($"Registered index: {indexName} (Collection: {collectionType.Name})");
        }
    }

    public void RegisterProjection(Type projectionType, Type indexType)
    {
        var indexName = indexType.Name;

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
            return _byCollectionType.TryGetValue(collectionType, out var registration) ? registration : null;
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
            return _byCollectionType.Values.Any(r => r.ProjectionType == type);
        }
    }

    /// <summary>
    /// Generic fallback: walks the type hierarchy looking for the first generic base class
    /// and returns its first generic argument as the collection type.
    /// Storage providers should prefer RegisterIndex(string, Type, Type) for precise control.
    /// </summary>
    private static Type? GetGenericArgumentFromBaseClass(Type indexType)
    {
        var current = indexType.BaseType;
        while (current != null && current != typeof(object))
        {
            if (current.IsGenericType)
            {
                var args = current.GetGenericArguments();
                if (args.Length > 0)
                    return args[0];
            }
            current = current.BaseType;
        }
        return null;
    }
}
