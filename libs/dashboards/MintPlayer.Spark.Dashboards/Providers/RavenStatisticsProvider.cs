using Raven.Client.Documents;
using Raven.Client.Documents.Operations;

namespace MintPlayer.Spark.Dashboards.Providers;

/// <summary>
/// The built-in RavenDB statistics (D13): <c>DatabaseSize</c> (bytes on disk), <c>DocumentCount</c>,
/// <c>CollectionCount</c> and <c>Collections</c>, a table of <c>{ name, documents, sizeBytes }</c>, largest first.
/// </summary>
/// <remarks>
/// Read through the application's own document store and certificate (D24): no container gains access to RavenDB
/// for it. Two database-level statistics operations, no query.
/// </remarks>
public sealed class RavenStatisticsProvider(IDocumentStore store) : IDashboardProvider
{
    public async Task FillAsync(DashboardValues values, CancellationToken cancellationToken)
    {
        var maintenance = store.Maintenance.ForDatabase(store.Database);
        var collections = await maintenance.SendAsync(new GetDetailedCollectionStatisticsOperation(), cancellationToken);
        var database = await maintenance.SendAsync(new GetDetailedStatisticsOperation(), cancellationToken);

        values.Set("DatabaseSize", database.SizeOnDisk?.SizeInBytes);
        values.Set("DocumentCount", collections.CountOfDocuments);
        values.Set("CollectionCount", collections.Collections.Count);
        if (!values.IsSampling)
        {
            values.Set("Collections", collections.Collections.Values
                .OrderByDescending(c => c.Size?.SizeInBytes ?? 0)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => new Dictionary<string, object?>
                {
                    ["name"] = c.Name,
                    ["documents"] = c.CountOfDocuments,
                    ["sizeBytes"] = c.Size?.SizeInBytes,
                })
                .ToList());
        }
    }
}
