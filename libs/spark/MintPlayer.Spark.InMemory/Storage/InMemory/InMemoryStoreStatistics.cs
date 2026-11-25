using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Storage.InMemory;

/// <summary>The in-memory store's <see cref="ISparkStoreStatistics"/>: document counts per collection; memory has no size on disk.</summary>
[Register(typeof(ISparkStoreStatistics), ServiceLifetime.Singleton)]
internal sealed partial class InMemoryStoreStatistics : ISparkStoreStatistics
{
    [Inject] private readonly InMemoryDatabase database;

    public Task<SparkStoreStatistics> GetAsync(CancellationToken cancellationToken = default)
    {
        var collections = database.CollectionCounts()
            .Select(c => new SparkCollectionStatistics(c.Collection, c.Documents, null))
            .OrderByDescending(c => c.Documents)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(new SparkStoreStatistics(null, collections.Sum(c => c.Documents), collections));
    }
}
