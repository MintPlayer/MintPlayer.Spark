using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;

namespace MintPlayer.Spark.Storage.Raven;

/// <summary>
/// RavenDB's <see cref="ISparkStoreStatistics"/>: two database-level statistics operations through the application's own
/// document store and certificate, no query.
/// </summary>
[Register(typeof(ISparkStoreStatistics), ServiceLifetime.Singleton)]
internal sealed partial class RavenStoreStatistics : ISparkStoreStatistics
{
    [Inject] private readonly IDocumentStore store;

    public async Task<SparkStoreStatistics> GetAsync(CancellationToken cancellationToken = default)
    {
        var maintenance = store.Maintenance.ForDatabase(store.Database);
        var collections = await maintenance.SendAsync(new GetDetailedCollectionStatisticsOperation(), cancellationToken);
        var database = await maintenance.SendAsync(new GetDetailedStatisticsOperation(), cancellationToken);

        return new SparkStoreStatistics(
            database.SizeOnDisk?.SizeInBytes,
            collections.CountOfDocuments,
            [.. collections.Collections.Values
                .Select(c => new SparkCollectionStatistics(c.Name, c.CountOfDocuments, c.Size?.SizeInBytes))
                .OrderByDescending(c => c.SizeBytes ?? 0)
                .ThenBy(c => c.Name, StringComparer.Ordinal)]);
    }
}
