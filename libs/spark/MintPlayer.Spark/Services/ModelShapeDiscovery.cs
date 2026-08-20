using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Abstractions.Reflection;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Walks a <see cref="SparkContext"/> type and produces the set of entities the model covers —
/// the queryable roots plus the transitive closure of embedded complex types.
/// <para>
/// Lives here rather than in Abstractions because it needs <c>IRavenQueryable&lt;&gt;</c>,
/// <see cref="SparkContext"/> and <see cref="IIndexCatalog"/>, none of which Abstractions can see.
/// The hashing itself is in <see cref="SparkModelShape"/>, which stays dependency-free.
/// </para>
/// <para>
/// Takes the context <em>type</em>, never an instance: only property types are read, so this needs
/// no session, no service provider and no database.
/// </para>
/// </summary>
public static class ModelShapeDiscovery
{
    /// <summary>
    /// Discovers every entity in the model, ordered by full type name so callers get a stable
    /// sequence regardless of reflection order.
    /// </summary>
    public static IReadOnlyList<SparkModelType> Discover(Type sparkContextType, IIndexCatalog indexCatalog)
    {
        ArgumentNullException.ThrowIfNull(sparkContextType);
        ArgumentNullException.ThrowIfNull(indexCatalog);

        var discovered = new Dictionary<Type, SparkModelType>();
        var embedded = new Queue<Type>();

        foreach (var entityType in QueryableRoots(sparkContextType))
        {
            // Projection types are merged into their collection type's file rather than getting one
            // of their own, so they are not entities in their own right.
            if (entityType.IsSparkProjection())
                continue;

            // The catalog's default exists only when a projection-bearing index maps the entity,
            // which preserves the invariant the old registry code enforced by hand: an index
            // WITHOUT a projection contributes no indexName to the model file, so hashing one
            // would make the hash describe something the model does not record — deleting such an
            // index would move the hash with no accompanying model diff.
            var defaultEntry = indexCatalog.GetDefaultForCollectionType(entityType);
            discovered[entityType] = new SparkModelType(
                entityType,
                defaultEntry?.ProjectionType?.FullName,
                defaultEntry?.IndexName);

            CollectEmbedded(entityType, embedded);
            if (defaultEntry?.ProjectionType is not null)
                CollectEmbedded(defaultEntry.ProjectionType, embedded);
        }

        while (embedded.Count > 0)
        {
            var type = embedded.Dequeue();
            if (discovered.ContainsKey(type))
                continue;

            discovered[type] = new SparkModelType(type, null, null);
            CollectEmbedded(type, embedded);
        }

        return [.. discovered.Values.OrderBy(t => t.Type.FullName ?? t.Type.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Simple names of the entities exposed directly on the context. Feeds the context-roots hash,
    /// which is what notices a root being removed — per-entity hashes cannot see that, because the
    /// orphaned model file and its CLR class both still exist and still agree.
    /// </summary>
    public static IReadOnlyList<string> RootEntityNames(Type sparkContextType)
    {
        ArgumentNullException.ThrowIfNull(sparkContextType);

        return [.. QueryableRoots(sparkContextType)
            .Where(t => !t.IsSparkProjection())
            .Select(t => t.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)];
    }

    private static IEnumerable<Type> QueryableRoots(Type sparkContextType)
        => sparkContextType.GetCachedProperties()
            .Where(p => p.PropertyType.IsGenericType
                     && p.PropertyType.GetGenericTypeDefinition() == typeof(IRavenQueryable<>))
            .Select(p => p.PropertyType.GetGenericArguments().FirstOrDefault())
            .Where(t => t is not null)
            .Select(t => t!);

    private static void CollectEmbedded(Type entityType, Queue<Type> pending)
    {
        // Shares the model-property filter with the generator on purpose: discovery and attribute
        // generation must agree, or an ignored complex property would still contribute its type to
        // the hash while never appearing in the model.
        foreach (var property in entityType.GetSparkModelProperties())
        {
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            propertyType = SparkModelShape.GetCollectionElementType(propertyType) ?? propertyType;

            if (SparkModelShape.IsComplexType(propertyType))
                pending.Enqueue(propertyType);
        }
    }
}
