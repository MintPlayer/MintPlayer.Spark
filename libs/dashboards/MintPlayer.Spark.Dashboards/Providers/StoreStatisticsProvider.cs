using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Dashboards.Providers;

/// <summary>
/// The built-in database statistics (D13; #536 D45: on any store): <c>DatabaseSize</c> (bytes on disk, none in memory),
/// <c>DocumentCount</c>, <c>CollectionCount</c> and <c>Collections</c>, a table of <c>{ name, documents, sizeBytes }</c>,
/// largest first.
/// </summary>
/// <remarks>
/// Read through <see cref="ISparkStoreStatistics"/>, the application's own store and credentials (D24): no container
/// gains access to the database for it. RavenDB answers with two statistics operations; SQL Server and PostgreSQL count
/// each collection's table and read the sizes from the catalog.
/// </remarks>
public sealed class StoreStatisticsProvider(ISparkStoreStatistics statistics) : IDashboardProvider
{
    public async Task FillAsync(DashboardValues values, CancellationToken cancellationToken)
    {
        var current = await statistics.GetAsync(cancellationToken);

        values.Set("DatabaseSize", current.SizeBytes);
        values.Set("DocumentCount", current.Documents);
        values.Set("CollectionCount", current.Collections.Count);
        if (!values.IsSampling)
        {
            values.Set("Collections", current.Collections
                .Select(c => new Dictionary<string, object?>
                {
                    ["name"] = c.Name,
                    ["documents"] = c.Documents,
                    ["sizeBytes"] = c.SizeBytes,
                })
                .ToList());
        }
    }
}
