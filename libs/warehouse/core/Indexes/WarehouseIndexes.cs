using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Warehouse.Models;

namespace MintPlayer.Spark.Warehouse.Indexes;

/// <summary>
/// The warehouse's static indexes (PRD §4.5 "Index budget"), declared as data (commerce X19): the application's generator
/// turns every <c>[SparkIndexMap]</c> of one name into one multi-map index, so the warehouse libraries spend five static
/// indexes whatever they compose (W19). Every filtered or sorted library query is bound to one of them; point loads and
/// id-prefix loads need none, so library pages create no auto index.
/// </summary>
/// <remarks>
/// X19 declares maps only. <c>Warehouse_LedgerTotals</c>, a map-reduce in the PRD's table, is therefore a map of signed
/// ledger entries per balance, summed by the reconcile (<c>StockQueries.LedgerTotalsAsync</c>); a host may add the reduce
/// in the generated class's <c>OnInitialize</c> (PRD §13, WM3).
/// </remarks>
public static class WarehouseIndexes
{
    /// <summary>Deployed names (the generator turns <c>_</c> into <c>/</c>).</summary>
    public const string Stock = "Warehouse/Stock";
    public const string LedgerTotals = "Warehouse/LedgerTotals";
    public const string Work = "Warehouse/Work";
    public const string Catalog = "Warehouse/Catalog";
    public const string Documents = "Warehouse/Documents";

    /// <summary>Allocation candidates, put-away (same lot, nearest empty), stock and location grids.</summary>
    [SparkIndexMap("Warehouse_Stock")]
    public static Expression<Func<IEnumerable<StockBalance>, IEnumerable<WarehouseStockEntry>>> Balances { get; } =
        balances => from b in balances
                    select new WarehouseStockEntry
                    {
                        Kind = WarehouseStockEntry.BalanceKind,
                        SiteId = b.SiteId,
                        LocationId = b.LocationId,
                        ItemId = b.ItemId,
                        OrganizationId = b.OrganizationId,
                        LotCode = b.LotCode,
                        Expiry = b.Expiry,
                        OnHand = b.OnHand,
                        Allocated = b.Allocated,
                        Available = b.OnHand - b.Allocated,
                        Sellable = b.Sellable,
                        ReceivedAt = b.ReceivedAt,
                        Active = true,
                    };

    [SparkIndexMap("Warehouse_Stock")]
    public static Expression<Func<IEnumerable<WarehouseLocation>, IEnumerable<WarehouseStockEntry>>> Locations { get; } =
        locations => from l in locations
                     select new WarehouseStockEntry
                     {
                         Kind = WarehouseStockEntry.LocationKind,
                         SiteId = l.SiteId,
                         LocationId = l.Id,
                         ZoneId = l.ZoneId,
                         Code = l.Code,
                         Path = l.Path,
                         WalkSequence = l.WalkSequence,
                         Type = l.Type,
                         ItemId = l.FixedItemId,
                         Active = l.Active,
                     };

    /// <summary>WI3: every ledger entry as a signed delta per balance (two entries for a move).</summary>
    [SparkIndexMap("Warehouse_LedgerTotals", StoreAllFields = true)]
    public static Expression<Func<IEnumerable<StockMovement>, IEnumerable<WarehouseLedgerEntry>>> Ledger { get; } =
        movements => from m in movements
                     where m.Kind == StockMovementKind.Receive || m.Kind == StockMovementKind.Move || m.Kind == StockMovementKind.Pick
                           || m.Kind == StockMovementKind.Adjust || m.Kind == StockMovementKind.Ship
                     from e in new[]
                     {
                         new WarehouseLedgerEntry { BalanceId = m.FromBalanceId, Delta = m.Kind == StockMovementKind.Adjust ? m.Quantity : -m.Quantity, ItemId = m.ItemId, OrganizationId = m.OrganizationId, LotCode = m.LotCode, Kind = m.Kind, Reference = m.Reference, ServerTime = m.ServerTime },
                         new WarehouseLedgerEntry { BalanceId = m.ToBalanceId, Delta = m.Quantity, ItemId = m.ItemId, OrganizationId = m.OrganizationId, LotCode = m.LotCode, Kind = m.Kind, Reference = m.Reference, ServerTime = m.ServerTime },
                     }
                     where e.BalanceId != null
                     select e;

    /// <summary>My tasks, supervisor queues, live lists, productivity figures.</summary>
    [SparkIndexMap("Warehouse_Work")]
    public static Expression<Func<IEnumerable<WarehouseTask>, IEnumerable<WarehouseWorkEntry>>> Tasks { get; } =
        tasks => from t in tasks
                 select new WarehouseWorkEntry
                 {
                     Kind = t.Kind,
                     State = t.State,
                     SiteId = t.SiteId,
                     ZoneId = t.ZoneId,
                     LocationId = t.LocationId,
                     ItemId = t.ItemId,
                     AssignedTo = t.AssignedTo,
                     GroupId = t.GroupId,
                     OrganizationIds = t.OrganizationIds,
                     Priority = t.Priority,
                     WalkSequence = t.WalkSequence,
                     CreatedAt = t.CreatedAt,
                     CompletedAt = t.CompletedAt,
                 };

    /// <summary>Item search by SKU, name or GTIN; the registry grid.</summary>
    [SparkIndexMap("Warehouse_Catalog")]
    public static Expression<Func<IEnumerable<WarehouseItem>, IEnumerable<WarehouseCatalogEntry>>> Items { get; } =
        items => from i in items
                 select new WarehouseCatalogEntry
                 {
                     Kind = WarehouseCatalogEntry.ItemKind,
                     OrganizationId = i.OrganizationId,
                     Sku = i.Sku,
                     Name = i.Name,
                     ItemId = i.Id,
                     CatalogItemId = i.CatalogItemId,
                     Active = i.Active,
                 };

    [SparkIndexMap("Warehouse_Catalog")]
    public static Expression<Func<IEnumerable<FulfilmentCatalogItem>, IEnumerable<WarehouseCatalogEntry>>> CatalogItems { get; } =
        items => from c in items
                 select new WarehouseCatalogEntry
                 {
                     Kind = WarehouseCatalogEntry.CatalogItemKind,
                     ExternalOwnerId = c.OwnerId,
                     Sku = c.Sku,
                     Name = c.Name,
                     CatalogItemId = c.Id,
                     Codes = c.Gtins,
                     Active = !c.Discontinued,
                 };

    [SparkIndexMap("Warehouse_Catalog")]
    public static Expression<Func<IEnumerable<BarcodeRegistration>, IEnumerable<WarehouseCatalogEntry>>> Barcodes { get; } =
        barcodes => from r in barcodes
                    select new WarehouseCatalogEntry
                    {
                        Kind = WarehouseCatalogEntry.BarcodeKind,
                        OrganizationId = r.OrganizationId,
                        ItemId = r.ItemId,
                        Codes = new[] { r.Code },
                        Active = true,
                    };
}

/// <summary>One entry of <c>Warehouse/Stock</c>: a balance or a location.</summary>
public sealed class WarehouseStockEntry
{
    public const string BalanceKind = "Balance";
    public const string LocationKind = "Location";

    public string Kind { get; set; } = "";
    public string? SiteId { get; set; }
    public string? LocationId { get; set; }
    public string? ZoneId { get; set; }
    public string? Code { get; set; }
    public string? Path { get; set; }
    public long WalkSequence { get; set; }
    public WarehouseLocationType Type { get; set; }
    public string? ItemId { get; set; }
    public string? OrganizationId { get; set; }
    public string? LotCode { get; set; }
    public DateOnly? Expiry { get; set; }
    public decimal OnHand { get; set; }
    public decimal Allocated { get; set; }
    public decimal Available { get; set; }
    public bool Sellable { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public bool Active { get; set; }
}

/// <summary>One signed ledger entry of <c>Warehouse/LedgerTotals</c>.</summary>
public sealed class WarehouseLedgerEntry
{
    public string? BalanceId { get; set; }
    public decimal Delta { get; set; }
    public string? ItemId { get; set; }
    public string? OrganizationId { get; set; }
    public string? LotCode { get; set; }
    public StockMovementKind Kind { get; set; }
    public string? Reference { get; set; }
    public DateTimeOffset ServerTime { get; set; }
}

/// <summary>One entry of <c>Warehouse/Work</c>.</summary>
public sealed class WarehouseWorkEntry
{
    public WarehouseTaskKind Kind { get; set; }
    public WarehouseTaskState State { get; set; }
    public string? SiteId { get; set; }
    public string? ZoneId { get; set; }
    public string? LocationId { get; set; }
    public string? ItemId { get; set; }
    public string? AssignedTo { get; set; }
    public string? GroupId { get; set; }
    public List<string> OrganizationIds { get; set; } = [];
    public int Priority { get; set; }
    public long WalkSequence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>One entry of <c>Warehouse/Catalog</c>: a warehouse item, a shop's catalog item replica, or a barcode.</summary>
public sealed class WarehouseCatalogEntry
{
    public const string ItemKind = "Item";
    public const string CatalogItemKind = "CatalogItem";
    public const string BarcodeKind = "Barcode";

    public string Kind { get; set; } = "";
    public string? OrganizationId { get; set; }
    public string? ExternalOwnerId { get; set; }
    public string? Sku { get; set; }
    [Search]
    public string? Name { get; set; }
    public string? ItemId { get; set; }
    public string? CatalogItemId { get; set; }
    public IEnumerable<string>? Codes { get; set; }
    public bool Active { get; set; }
}

/// <summary>
/// One entry of <c>Warehouse/Documents</c> (Q-W26, WX16): Inbound, Outbound and Packing contribute their maps of receipts,
/// outbound orders, shipments and returns to this one index; the core declares the shape they agree on.
/// </summary>
public sealed class WarehouseDocumentEntry
{
    public string Kind { get; set; } = "";
    public string? SiteId { get; set; }
    public string? OrganizationId { get; set; }
    public string? State { get; set; }
    public string? Number { get; set; }
    public DateTimeOffset At { get; set; }
}
