using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Live;

/// <summary>The collections whose changes re-run a live query.</summary>
/// <remarks>
/// <para>
/// The query type's own collection, plus the collection its index maps when that is another one. A change
/// in a collection the query only reaches through <c>LoadDocument</c>, a reference's breadcrumb or a
/// multi-map index is not seen: such a query is re-run by <see cref="SparkLiveOptions.Revalidate"/>, or by
/// <see cref="Abstractions.Live.ISparkLiveNotifier.CollectionChanged"/> from the code that writes it.
/// </para>
/// <para>
/// Collections rather than <c>ForIndex</c>: under <c>Source = Actions</c> there are no index notifications,
/// and the evaluation waits for the indexes over the changed collection anyway (<see cref="SparkIndexWait"/>),
/// so one write costs one evaluation per viewer instead of two (the write, then the index batch).
/// </para>
/// </remarks>
internal static class LiveQueryCollections
{
    public static IReadOnlyCollection<string> Resolve(SparkQuery query, IModelLoader modelLoader, IIndexCatalog indexCatalog, IDocumentStore store)
    {
        var collections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var definition = query.EntityType is null ? null : modelLoader.GetEntityTypeByName(query.EntityType);
        if (definition is null)
            return collections;

        if (SparkTypeResolver.ResolveClrType(definition.ClrType) is { } entityType)
            collections.Add(store.Conventions.GetCollectionName(entityType));

        var indexName = !string.IsNullOrEmpty(query.IndexName) ? query.IndexName : definition.IndexName;
        if (!string.IsNullOrEmpty(indexName) && indexCatalog.GetByIndexName(indexName)?.CollectionType is { } mapped)
            collections.Add(store.Conventions.GetCollectionName(mapped));

        return collections;
    }
}
