using MintPlayer.Spark.Warehouse.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Warehouse.Stock;

/// <summary>
/// One stock command's session: the documents it read (each loaded once), the balances and stock levels it created or
/// changed, and the creation rule — a new document is stored with a must-not-exist change vector, or plainly in a
/// cluster-wide transaction (whose atomic guards make a concurrent create conflict).
/// </summary>
internal sealed class StockSession(IAsyncDocumentSession session, bool clusterWide, DateTimeOffset now, CancellationToken token)
{
    private readonly Dictionary<string, WarehouseLocation?> locations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WarehouseZone?> zones = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WarehouseItem?> items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StockLot?> lots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StockBalance?> balances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StockLevel> levels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<StockLevel> changedLevels = [];

    public IAsyncDocumentSession Session { get; } = session;

    public DateTimeOffset Now { get; } = now;

    public CancellationToken Token { get; } = token;

    public IReadOnlyCollection<StockLevel> ChangedLevels => changedLevels;

    /// <summary>
    /// Sellable stock (Q-W5): released (or untracked) stock in storage, pick-face, staging, consolidation and pack-station
    /// locations. Receiving, quarantine and returns locations, quarantine and returns zones, and quarantined or recalled
    /// lots are not sellable.
    /// </summary>
    public static bool IsSellableType(WarehouseLocationType type) => type is WarehouseLocationType.Storage or WarehouseLocationType.PickFace
        or WarehouseLocationType.Staging or WarehouseLocationType.Consolidation or WarehouseLocationType.PackStation;

    public async Task<bool> IsSellableAsync(WarehouseLocation location, StockLot? lot)
    {
        if (!IsSellableType(location.Type) || lot is { Status: not StockLotStatus.Released })
            return false;
        if (location.ZoneId is null)
            return true;
        var zone = await ZoneAsync(location.ZoneId);
        return zone?.Kind is not (WarehouseZoneKind.Quarantine or WarehouseZoneKind.Returns);
    }

    public Task<WarehouseLocation?> LocationAsync(string id) => LoadCachedAsync(locations, id);

    public Task<WarehouseZone?> ZoneAsync(string id) => LoadCachedAsync(zones, id);

    public Task<WarehouseItem?> ItemAsync(string id) => LoadCachedAsync(items, id);

    public Task<StockLot?> LotAsync(string id) => LoadCachedAsync(lots, id);

    public Task<StockBalance?> BalanceAsync(string id) => LoadCachedAsync(balances, id);

    /// <summary>A balance found by query: tracked so it is written back with the session (and checked).</summary>
    public void Track(StockBalance balance) => balances.TryAdd(balance.Id!, balance);

    /// <summary>The lot a receipt books into: loaded, or created Released with the receipt's expiry and reference.</summary>
    public async Task<StockLot?> LotForReceiptAsync(WarehouseItem item, string? lotCode, DateOnly? expiry, string? receiptReference)
    {
        if (string.IsNullOrWhiteSpace(lotCode))
            return null;
        var id = StockLot.IdFor(item.Id!, lotCode);
        var lot = await LotAsync(id);
        if (lot is not null)
            return lot;

        lot = new StockLot
        {
            Id = id,
            OrganizationId = item.OrganizationId,
            ItemId = item.Id!,
            LotCode = lotCode.Trim(),
            Expiry = expiry,
            Status = StockLotStatus.Released,
            ReceiptReference = receiptReference,
        };
        await StoreNewAsync(lot, id);
        lots[id] = lot;
        return lot;
    }

    /// <summary>
    /// The balance of (location, item, lot), created when missing (the atomic first put-away). A new balance takes its
    /// sellability from the location and the lot's current status.
    /// </summary>
    public async Task<StockBalance> BalanceForAsync(WarehouseLocation location, WarehouseItem item, StockLot? lot)
    {
        var id = StockBalance.IdFor(location.Id!, item.Id!, lot?.LotCode);
        var balance = await BalanceAsync(id);
        if (balance is not null)
            return balance;

        balance = new StockBalance
        {
            Id = id,
            OrganizationId = item.OrganizationId,
            SiteId = location.SiteId,
            LocationId = location.Id!,
            ItemId = item.Id!,
            LotCode = lot?.LotCode,
            Expiry = lot?.Expiry,
            Sellable = await IsSellableAsync(location, lot),
            UpdatedAt = Now,
        };
        await StoreNewAsync(balance, id);
        balances[id] = balance;
        return balance;
    }

    /// <summary>The stock level of the item's owner and the item, created when missing; marked changed.</summary>
    public async Task<StockLevel> LevelAsync(WarehouseItem item)
    {
        var id = StockLevel.IdFor(item.OrganizationId ?? "", item.Id!);
        if (!levels.TryGetValue(id, out var level))
        {
            level = await Session.LoadAsync<StockLevel>(id, Token);
            if (level is null)
            {
                level = new StockLevel { Id = id, OrganizationId = item.OrganizationId, ItemId = item.Id!, Sku = item.Sku };
                await StoreNewAsync(level, id);
            }
            levels[id] = level;
        }
        level.Sku = item.Sku;
        changedLevels.Add(level);
        return level;
    }

    public void LevelChanged(StockLevel level) => changedLevels.Add(level);

    /// <summary>
    /// A command that leaves units of <paramref name="lot"/> sellable (a receipt, put-away or move into a sellable
    /// location, a positive adjustment, an allocation) writes the lot, so it commits only against the lot status it read:
    /// a recall (or quarantine) committing meanwhile makes this session conflict and retry with the new status, and no unit
    /// of a recalled lot is ever counted sellable (lead decision 2026-10-20). The lot is then as hot as its item's stock level.
    /// </summary>
    public void ObserveLot(StockLot? lot)
    {
        if (lot is not null)
            lot.Observed++;
    }

    /// <summary>A location with ledger history can only be deactivated (PRD §4.5); written once, not on every movement.</summary>
    public void TouchHistory(WarehouseLocation location)
    {
        if (!location.HasHistory)
            location.HasHistory = true;
    }

    public Task StoreNewAsync(object entity, string id)
        => clusterWide
            ? Session.StoreAsync(entity, id, Token)
            : Session.StoreAsync(entity, string.Empty, id, Token);

    private async Task<T?> LoadCachedAsync<T>(Dictionary<string, T?> cache, string id) where T : class
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        if (cache.TryGetValue(id, out var cached))
            return cached;
        var loaded = await Session.LoadAsync<T>(id, Token);
        cache[id] = loaded;
        return loaded;
    }
}
