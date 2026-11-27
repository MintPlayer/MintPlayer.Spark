using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Services;

/// <summary>
/// #551 D20: a model that binds an index (an entity type's or a query's <c>indexName</c>) on a store without the
/// <see cref="SparkStoreCapabilities.Indexes"/> capability is refused at startup, naming every binding. Without this the
/// application starts and the binding fails on its first query ("no deployed index has that name").
/// </summary>
internal static class SparkIndexBindingStartupCheck
{
    /// <summary>
    /// The bindings <paramref name="store"/> cannot serve, as messages; empty when it has indexes or is absent. A binding
    /// to one of <paramref name="viewNames"/> is served everywhere: every provider renders a <c>SparkView</c> (#536 D32).
    /// </summary>
    public static IReadOnlyList<string> Unmet(IModelLoader modelLoader, ISparkStore? store, IReadOnlySet<string>? viewNames = null)
    {
        // No store is the requirement check's message, not this one.
        if (store is null || store.Capabilities.HasFlag(SparkStoreCapabilities.Indexes))
            return [];

        bool Unserved(string? indexName) => !string.IsNullOrEmpty(indexName) && viewNames?.Contains(indexName) != true;

        var bindings = modelLoader.GetEntityTypes()
            .Where(t => Unserved(t.IndexName))
            .Select(t => $"entity type '{t.Name}' (indexName '{t.IndexName}')")
            .Concat(modelLoader.GetQueries()
                .Where(q => Unserved(q.IndexName))
                .Select(q => $"query '{q.Name}' (indexName '{q.IndexName}')"))
            .ToList();

        return bindings.Count == 0
            ? []
            :
            [
                $"The {store.ProviderName} provider has no indexes, but the model binds one: {string.Join(", ", bindings)}. "
                + "Remove the indexName (the query then reads the collection), declare the index as a SparkView<TRow>, "
                + "or use a provider with indexes (one whose capabilities include Indexes).",
            ];
    }
}
