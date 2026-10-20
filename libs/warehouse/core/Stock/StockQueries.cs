using MintPlayer.Spark.Warehouse.Indexes;
using MintPlayer.Spark.Warehouse.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Warehouse.Stock;

/// <summary>
/// The library's stock queries, each bound to a declared index (never an auto index, W19) and filtered explicitly by owner
/// (raw session queries are not row-filtered, <c>IOrganizationOwned.cs:21-22</c>; WI5). Values travel as LINQ parameters,
/// never spliced into RQL. <paramref name="visibleOwners"/>: null = every owner (staff, system work); otherwise only these.
/// </summary>
public static class StockQueries
{
    /// <summary>The balances of one lot (recall, quarantine), fresh: the query waits for the index to catch up.</summary>
    public static async Task<IReadOnlyList<StockBalance>> BalancesOfLotAsync(IAsyncDocumentSession session, string itemId, string lotCode, CancellationToken cancellationToken)
        => await session.Query<WarehouseStockEntry>(WarehouseIndexes.Stock)
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
            .Where(e => e.Kind == WarehouseStockEntry.BalanceKind && e.ItemId == itemId && e.LotCode == lotCode)
            .OfType<StockBalance>()
            .Take(4096)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Allocation candidates (PRD §4.7): sellable balances of an item with stock available at a site. The allocation rule
    /// ranks them; the engine then claims each under optimistic concurrency, so a stale entry is skipped, never oversold.
    /// </summary>
    public static async Task<IReadOnlyList<StockBalance>> CandidatesAsync(IAsyncDocumentSession session, string siteId, string itemId,
        IReadOnlyCollection<string>? visibleOwners, CancellationToken cancellationToken, int take = 256)
    {
        var query = session.Query<WarehouseStockEntry>(WarehouseIndexes.Stock)
            .Where(e => e.Kind == WarehouseStockEntry.BalanceKind && e.SiteId == siteId && e.ItemId == itemId && e.Sellable && e.Available > 0);
        if (visibleOwners is not null)
            query = query.Where(e => e.OrganizationId.In(visibleOwners));
        return await query.OfType<StockBalance>().Take(take).ToListAsync(cancellationToken);
    }

    /// <summary>The stock of an item per location (the stock grid), owner-filtered.</summary>
    public static IRavenQueryable<WarehouseStockEntry> Stock(IAsyncDocumentSession session, IReadOnlyCollection<string>? visibleOwners)
    {
        var query = session.Query<WarehouseStockEntry>(WarehouseIndexes.Stock).Where(e => e.Kind == WarehouseStockEntry.BalanceKind && e.OnHand != 0);
        return visibleOwners is null ? query : query.Where(e => e.OrganizationId.In(visibleOwners));
    }

    /// <summary>The fixed pick faces of a site (min/max replenishment, §4.5): active pick-face locations with a fixed item.</summary>
    public static async Task<IReadOnlyList<WarehouseLocation>> FixedPickFacesAsync(IAsyncDocumentSession session, string? siteId, CancellationToken cancellationToken)
    {
        var query = session.Query<WarehouseStockEntry>(WarehouseIndexes.Stock)
            .Where(e => e.Kind == WarehouseStockEntry.LocationKind && e.Type == WarehouseLocationType.PickFace && e.Active && e.ItemId != null);
        if (siteId is not null)
            query = query.Where(e => e.SiteId == siteId);
        return await query.OfType<WarehouseLocation>().Take(4096).ToListAsync(cancellationToken);
    }

    /// <summary>Released, non-pick-face sellable balances of an item at a site, the sources of a replenishment.</summary>
    public static async Task<IReadOnlyList<StockBalance>> ReplenishmentSourcesAsync(IAsyncDocumentSession session, string siteId, string itemId, string pickFaceId, CancellationToken cancellationToken)
        => await session.Query<WarehouseStockEntry>(WarehouseIndexes.Stock)
            .Where(e => e.Kind == WarehouseStockEntry.BalanceKind && e.SiteId == siteId && e.ItemId == itemId && e.Sellable && e.Available > 0 && e.LocationId != pickFaceId)
            .OfType<StockBalance>()
            .Take(256)
            .ToListAsync(cancellationToken);

    /// <summary>WI3: the ledger's sum per balance, from <c>Warehouse/LedgerTotals</c> (summed here; X19 declares no reduce).</summary>
    public static async Task<IReadOnlyDictionary<string, decimal>> LedgerTotalsAsync(IAsyncDocumentSession session, string itemId, CancellationToken cancellationToken)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var query = session.Query<WarehouseLedgerEntry>(WarehouseIndexes.LedgerTotals)
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
            .Where(e => e.ItemId == itemId)
            .ProjectInto<WarehouseLedgerEntry>();
        await using var stream = await session.Advanced.StreamAsync(query, cancellationToken);
        while (await stream.MoveNextAsync())
        {
            var entry = stream.Current.Document;
            if (entry.BalanceId is null)
                continue;
            totals[entry.BalanceId] = totals.GetValueOrDefault(entry.BalanceId) + entry.Delta;
        }
        return totals;
    }

    /// <summary>The ledger entries that took a lot (or an untracked item) out of stock by shipping, for the recall trace.</summary>
    public static async Task<IReadOnlyList<StockMovement>> ShipmentsOfAsync(IAsyncDocumentSession session, string itemId, string? lotCode,
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var query = session.Query<WarehouseLedgerEntry>(WarehouseIndexes.LedgerTotals)
            .Where(e => e.ItemId == itemId && e.Kind == StockMovementKind.Ship);
        if (lotCode is not null)
            query = query.Where(e => e.LotCode == lotCode);
        if (from is { } f)
            query = query.Where(e => e.ServerTime >= f);
        if (to is { } t)
            query = query.Where(e => e.ServerTime < t);
        return await query.OfType<StockMovement>().Take(10_000).ToListAsync(cancellationToken);
    }
}
