using System.Reflection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
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
        var collectionType = GetCollectionTypeFromIndex(indexType);
        if (collectionType == null)
        {
            Console.WriteLine($"Warning: Could not determine collection type for index {indexType.Name}");
            return;
        }

        var indexName = GetIndexName(indexType);

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

    private static Type? GetCollectionTypeFromIndex(Type indexType)
    {
        var current = indexType;
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
    }

    private static string GetIndexName(Type indexType)
    {
        // RavenDB uses the class name as the index name
        return indexType.Name;
    }
}
