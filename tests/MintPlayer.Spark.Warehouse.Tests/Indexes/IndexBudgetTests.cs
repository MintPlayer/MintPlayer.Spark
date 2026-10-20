using System.Reflection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Warehouse.Barcodes;
using MintPlayer.Spark.Warehouse.Indexes;
using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Stock;
using MintPlayer.Spark.Warehouse.Work;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark.Warehouse.Tests.Indexes;

/// <summary>
/// W19 at CI size (PRD §7, §4.5 "Index budget"). RavenDB lifted the Community caps (12 static, 24 auto) on 2024-05-22, so
/// the budget checks are kept as practice, and the check that always applies is asserted: library code creates no auto
/// index. The static count is asserted, never assumed (plan rule "Licence realism").
/// </summary>
public class IndexBudgetTests : WarehouseTestBase
{
    [Fact]
    public void The_core_declares_at_most_the_budgeted_static_indexes()
    {
        var declared = typeof(WarehouseIndexes).Assembly.GetTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Static).Cast<MemberInfo>().Concat(t.GetFields(BindingFlags.Public | BindingFlags.Static)))
            .SelectMany(m => m.GetCustomAttributes<SparkIndexMapAttribute>())
            .Select(a => a.IndexName)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        string.Join(",", declared).Should().Be("Warehouse_Catalog,Warehouse_LedgerTotals,Warehouse_Stock,Warehouse_Work",
            "Warehouse_Documents gets its maps from Inbound, Outbound and Packing (Q-W26), so the core declares four of the five");
    }

    [Fact]
    public async Task Every_library_query_runs_on_a_declared_index_and_creates_no_auto_index()
    {
        await SeedSiteAsync();
        var shelf = await LocationAsync("A-01", WarehouseLocationType.Storage);
        var face = await LocationAsync("PF-1", WarehouseLocationType.PickFace);
        var item = await ItemAsync(OwnerA, "SKU-1", ItemTracking.LotAndExpiry);
        await StockEngine.ReceiveAsync(new ReceiveStock(Op("r1"), shelf, item, 5, "L1"));
        await StockEngine.AllocateAsync(new AllocateStock(Op("a1"), StockBalance.IdFor(shelf, item, "L1"), 1));
        await StockEngine.ShipAsync(new ShipStock(Op("s1"), StockBalance.IdFor(shelf, item, "L1"), 1, "web"));
        await WaitForIndexesAsync();

        // The library's read paths, each once (the stock engine's recall query included).
        using (var session = Store.OpenAsyncSession())
        {
            await StockQueries.BalancesOfLotAsync(session, item, "L1", CancellationToken.None);
            await StockQueries.CandidatesAsync(session, Site, item, [OwnerA], CancellationToken.None);
            await StockQueries.Stock(session, [OwnerA]).ToListAsync();
            await StockQueries.FixedPickFacesAsync(session, Site, CancellationToken.None);
            await StockQueries.ReplenishmentSourcesAsync(session, Site, item, face, CancellationToken.None);
            await StockQueries.LedgerTotalsAsync(session, item, CancellationToken.None);
            await StockQueries.ShipmentsOfAsync(session, item, "L1", null, null, CancellationToken.None);
        }
        await Get<IWarehouseTasks>().QueueAsync(Site, [OwnerA], "users/a");
        await Get<IWarehouseReplenishment>().SweepAsync(Site);
        await Get<IRecallTrace>().TraceAsync(item, "L1");
        await Get<IBarcodeResolver>().ResolveAsync(new BarcodeScanContext("5412345000013", Site, [OwnerA]));
        await StockEngine.ChangeLotStatusAsync(new ChangeLotStatus(Op("q1"), StockLot.IdFor(item, "L1"), StockLotStatus.Quarantine));

        var names = await Store.Maintenance.SendAsync(new GetIndexNamesOperation(0, 1024));
        names.Where(n => n.StartsWith("Auto/", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty("library pages and services create no auto index (W19)");
        names.Count(n => n.StartsWith("Warehouse/", StringComparison.Ordinal)).Should().Be(4);
    }
}
