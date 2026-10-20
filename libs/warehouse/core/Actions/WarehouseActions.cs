using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Warehouse.CustomActions;
using MintPlayer.Spark.Warehouse.Indexes;
using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Owners;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Warehouse.Actions;

// The library's persistent-object actions (found by ActionsResolver; PRD §4.5). Every query an owner-bearing type
// lists through is a Custom.* query bound to a declared index or a plain collection query for the small lists, and
// filters explicitly by the caller's owners (raw session queries are not row-filtered, WI5). Organizations' row policy
// applies on top for every IOrganizationOwned type.

public partial class WarehouseSiteActions : DefaultPersistentObjectActions<WarehouseSite>
{
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>The query <c>WarehouseSites</c>: a small list, no index needed.</summary>
    public IQueryable<WarehouseSite> WarehouseSites() => session.Query<WarehouseSite>();
}

public partial class WarehouseZoneActions : DefaultPersistentObjectActions<WarehouseZone>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<WarehouseZone> WarehouseZones() => session.Query<WarehouseZone>();
}

/// <summary>Locations: listed through <c>Warehouse/Stock</c>; a location with ledger history is never deleted (deactivate it).</summary>
public partial class WarehouseLocationActions : DefaultPersistentObjectActions<WarehouseLocation>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<WarehouseLocation> WarehouseLocations()
        => session.Query<WarehouseStockEntry>(WarehouseIndexes.Stock)
            .Where(e => e.Kind == WarehouseStockEntry.LocationKind)
            .OrderBy(e => e.WalkSequence)
            .OfType<WarehouseLocation>();

    public override Task<bool> IsAllowedAsync(string action, WarehouseLocation entity)
        => Task.FromResult(action != "Delete" || !entity.HasHistory);
}

public partial class WarehouseOwnerLinkActions : DefaultPersistentObjectActions<WarehouseOwnerLink>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<WarehouseOwnerLink> WarehouseOwnerLinks() => session.Query<WarehouseOwnerLink>();
}

public partial class ReasonCodeActions : DefaultPersistentObjectActions<ReasonCode>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<ReasonCode> ReasonCodes() => session.Query<ReasonCode>();
}

/// <summary>Items: listed through <c>Warehouse/Catalog</c>, owner-filtered.</summary>
public partial class WarehouseItemActions : DefaultPersistentObjectActions<WarehouseItem>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IWarehouseOwnerScope ownerScope;

    public async Task<IQueryable<WarehouseItem>> WarehouseItems()
    {
        var owners = await ownerScope.VisibleOwnersAsync(nameof(WarehouseItem));
        var query = session.Query<WarehouseCatalogEntry>(WarehouseIndexes.Catalog).Where(e => e.Kind == WarehouseCatalogEntry.ItemKind);
        if (owners is not null)
            query = query.Where(e => e.OrganizationId.In(owners));
        return query.OrderBy(e => e.Sku).OfType<WarehouseItem>();
    }
}

public partial class BarcodeRegistrationActions : DefaultPersistentObjectActions<BarcodeRegistration>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IWarehouseOwnerScope ownerScope;

    public async Task<IQueryable<BarcodeRegistration>> BarcodeRegistrations()
    {
        var owners = await ownerScope.VisibleOwnersAsync(nameof(BarcodeRegistration));
        var query = session.Query<WarehouseCatalogEntry>(WarehouseIndexes.Catalog).Where(e => e.Kind == WarehouseCatalogEntry.BarcodeKind);
        if (owners is not null)
            query = query.Where(e => e.OrganizationId.In(owners));
        return query.OfType<BarcodeRegistration>();
    }
}

/// <summary>Balances are read-only (PRD §4.5): no rights to write them; corrections are adjustments.</summary>
public partial class StockBalanceActions : DefaultPersistentObjectActions<StockBalance>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IWarehouseOwnerScope ownerScope;

    public async Task<IQueryable<StockBalance>> StockBalances()
        => Stock.StockQueries.Stock(session, await ownerScope.VisibleOwnersAsync(nameof(StockBalance))).OrderBy(e => e.LocationId).OfType<StockBalance>();

    public override Task<bool> IsAllowedAsync(string action, StockBalance entity)
        => Task.FromResult(action is "Read" or "Query");
}

/// <summary>The ledger: append-only, never edited or deleted (PRD §4.5).</summary>
public partial class StockMovementActions : DefaultPersistentObjectActions<StockMovement>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IWarehouseOwnerScope ownerScope;

    public async Task<IQueryable<StockMovement>> StockMovements()
    {
        var owners = await ownerScope.VisibleOwnersAsync(nameof(StockMovement));
        var query = session.Query<WarehouseLedgerEntry>(WarehouseIndexes.LedgerTotals);
        if (owners is not null)
            query = query.Where(e => e.OrganizationId.In(owners));
        return query.OrderByDescending(e => e.ServerTime).OfType<StockMovement>();
    }

    public override Task<bool> IsAllowedAsync(string action, StockMovement entity)
        => Task.FromResult(action is "Read" or "Query");
}

public partial class StockLotActions : DefaultPersistentObjectActions<StockLot>
{
    public override Task<bool> IsAllowedAsync(string action, StockLot entity)
        => Task.FromResult(action is "Read" or "Query");
}

public partial class StockLevelActions : DefaultPersistentObjectActions<StockLevel>
{
    public override Task<bool> IsAllowedAsync(string action, StockLevel entity)
        => Task.FromResult(action is "Read" or "Query");
}

/// <summary>Tasks: listed through <c>Warehouse/Work</c>, owner-filtered; changed only through the task envelope.</summary>
public partial class WarehouseTaskActions : DefaultPersistentObjectActions<WarehouseTask>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IWarehouseOwnerScope ownerScope;

    public async Task<IQueryable<WarehouseTask>> WarehouseTasks()
    {
        var owners = await ownerScope.VisibleOwnersAsync(nameof(WarehouseTask));
        var query = session.Query<WarehouseWorkEntry>(WarehouseIndexes.Work);
        if (owners is not null)
            query = query.Where(e => e.OrganizationIds.Any(o => o.In(owners)));
        return query.OrderBy(e => e.WalkSequence).OfType<WarehouseTask>();
    }

    public override Task<bool> IsAllowedAsync(string action, WarehouseTask entity)
        => Task.FromResult(action is "Read" or "Query");
}

/// <summary>The command form is never stored: its values go to the move/adjust custom actions.</summary>
public partial class WarehouseStockCommandActions : DefaultPersistentObjectActions<WarehouseStockCommand>
{
    public override Task<bool> IsAllowedAsync(string action, WarehouseStockCommand entity)
        => Task.FromResult(action is not ("New" or "Edit" or "Delete"));
}
