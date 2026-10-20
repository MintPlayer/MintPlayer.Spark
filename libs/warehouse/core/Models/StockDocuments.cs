using MintPlayer.Spark.Organizations;

namespace MintPlayer.Spark.Warehouse.Models;

/// <summary>A lot's status (PRD §4.5, WD4). Only <see cref="Released"/> stock is sellable and allocatable.</summary>
public enum StockLotStatus
{
    Released,
    Quarantine,
    /// <summary>Round 5 (commerce GA7): blocked at once from allocation, pick and ship; traced forward.</summary>
    Recalled,
}

/// <summary>A lot of an item (PRD §4.5): <c>{itemId}/lots/{lotCode}</c>. Expiry is a <see cref="DateOnly"/> (§4.14).</summary>
public sealed class StockLot : IOrganizationOwned
{
    public static string IdFor(string itemId, string lotCode) => $"{itemId}/lots/{Uri.EscapeDataString(lotCode.Trim())}";

    public string? Id { get; set; }

    public string? OrganizationId { get; set; }

    public string ItemId { get; set; } = "";

    public string LotCode { get; set; } = "";

    public DateOnly? Expiry { get; set; }

    public StockLotStatus Status { get; set; }

    /// <summary>The receipt the lot first arrived with (the recall trace lists lots of the same receipt).</summary>
    public string? ReceiptReference { get; set; }

    public DateTimeOffset? RecalledAt { get; set; }

    public string? RecallReason { get; set; }

    /// <summary>
    /// Bumped by every command that leaves units of this lot sellable, so such a command and a status change of the lot
    /// conflict instead of interleaving (the recall race, PRD §13 WM3).
    /// </summary>
    public long Observed { get; set; }
}

/// <summary>
/// Stock of one item and lot at one location (PRD §4.5, WD4): <c>{locationId}/{itemId}/{lotKey}</c>. Written only by the
/// stock engine, in the same session as its <see cref="StockMovement"/> and <see cref="StockLevel"/> (WI3, WI6); read-only
/// in the UI. Invariants: <c>OnHand ≥ 0</c> (WI1) and <c>0 ≤ Allocated ≤ OnHand</c> (WI2).
/// </summary>
public sealed class StockBalance : IOrganizationOwned
{
    /// <summary>The lot key of stock without a lot.</summary>
    public const string NoLot = "-";

    public static string IdFor(string locationId, string itemId, string? lotCode)
        => $"{locationId}/{itemId}/{(string.IsNullOrWhiteSpace(lotCode) ? NoLot : Uri.EscapeDataString(lotCode.Trim()))}";

    public string? Id { get; set; }

    public string? OrganizationId { get; set; }

    public string SiteId { get; set; } = "";

    public string LocationId { get; set; } = "";

    public string ItemId { get; set; } = "";

    public string? LotCode { get; set; }

    /// <summary>A copy of the lot's expiry, for FEFO without a load.</summary>
    public DateOnly? Expiry { get; set; }

    /// <summary>Base units on the shelf.</summary>
    public decimal OnHand { get; set; }

    /// <summary>Base units claimed by order lines.</summary>
    public decimal Allocated { get; set; }

    /// <summary>Whether this stock counts as sellable (Q-W5): location type and lot status; kept in step with <see cref="StockLevel"/>.</summary>
    public bool Sellable { get; set; }

    /// <summary>When stock first arrived here (FIFO; FEFO ties).</summary>
    public DateTimeOffset? ReceivedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>What a movement did (PRD §4.5). Only the kinds that change <c>OnHand</c> enter the WI3 ledger sum.</summary>
public enum StockMovementKind
{
    /// <summary>Into a location from outside (receipt, return restock).</summary>
    Receive,
    /// <summary>From one balance to another (put-away, replenishment, move, put-back).</summary>
    Move,
    /// <summary>A correction with a reason code (count variance, damage, found stock).</summary>
    Adjust,
    /// <summary>A pick: a move that carries its allocation to the tote or staging location.</summary>
    Pick,
    /// <summary>Out of the warehouse at ship-confirm: on hand and allocation down, the channel's shipped total up (WI6).</summary>
    Ship,
    /// <summary>An allocation claimed or released; changes <c>Allocated</c> only.</summary>
    Allocate,
    /// <summary>A change of a channel's open (accepted, unshipped) quantity; changes the stock level only.</summary>
    Open,
    /// <summary>A lot's status changed (quarantine, release, recall); changes <c>Sellable</c> and the stock level.</summary>
    Status,
}

/// <summary>
/// One ledger entry (PRD §4.5, "A ledger, not History"): append-only, keyed by the command's op id, so a retried or
/// duplicated command is applied at most once (WI4). The user and the device are stamped explicitly (raw writes are not
/// audit-stamped).
/// </summary>
public sealed class StockMovement : IOrganizationOwned
{
    public const string IdPrefix = "stock-movements/";

    public static string IdFor(string opId) => IdPrefix + Uri.EscapeDataString(opId.Trim());

    public string? Id { get; set; }

    public string OpId { get; set; } = "";

    public StockMovementKind Kind { get; set; }

    public string? OrganizationId { get; set; }

    public string ItemId { get; set; } = "";

    public string? LotCode { get; set; }

    public string? FromBalanceId { get; set; }

    public string? ToBalanceId { get; set; }

    public string? FromLocationId { get; set; }

    public string? ToLocationId { get; set; }

    /// <summary>Base units; for <see cref="StockMovementKind.Adjust"/> and <see cref="StockMovementKind.Allocate"/> signed.</summary>
    public decimal Quantity { get; set; }

    /// <summary>The channel, for <see cref="StockMovementKind.Ship"/> and <see cref="StockMovementKind.Open"/>.</summary>
    public string? Channel { get; set; }

    public string? ReasonCode { get; set; }

    /// <summary>What the movement was for: a task, an order line, a shipment line, a receipt.</summary>
    public string? Reference { get; set; }

    public string? UserId { get; set; }

    public string? DeviceId { get; set; }

    /// <summary>The device's clock (offline commands): recorded, never used for ordering (§4.14).</summary>
    public DateTimeOffset? DeviceTime { get; set; }

    public DateTimeOffset ServerTime { get; set; }
}

/// <summary>A channel's part of a <see cref="StockLevel"/> (§4.4).</summary>
public sealed class StockLevelChannel
{
    public string Channel { get; set; } = "";

    /// <summary>Accepted, unshipped quantity of this channel's orders.</summary>
    public decimal Open { get; set; }

    /// <summary>Cumulative units shipped for this channel; monotonic.</summary>
    public decimal ShippedTotal { get; set; }
}

/// <summary>
/// The feed's source (PRD §4.4): one document per (owner, item), <c>stock-levels/{organizationId}/{itemId}</c>, updated in
/// the movement's own session, so <see cref="OnHandSellable"/> and a channel's <see cref="StockLevelChannel.ShippedTotal"/>
/// change in one transaction (WI6). Published per linked shop as <c>FulfilmentStockLevel</c>.
/// </summary>
public sealed class StockLevel : IOrganizationOwned
{
    public const string IdPrefix = "stock-levels/";

    public static string IdFor(string organizationId, string itemId) => $"{IdPrefix}{organizationId}/{itemId}";

    public string? Id { get; set; }

    public string? OrganizationId { get; set; }

    public string ItemId { get; set; } = "";

    public string Sku { get; set; } = "";

    public decimal OnHandSellable { get; set; }

    public List<StockLevelChannel> Channels { get; set; } = [];

    /// <summary>Monotonic; consumers ignore anything older than what they hold.</summary>
    public long Version { get; set; }

    internal StockLevelChannel Channel(string channel)
    {
        var existing = Channels.FirstOrDefault(c => string.Equals(c.Channel, channel, StringComparison.Ordinal));
        if (existing is not null)
            return existing;
        existing = new StockLevelChannel { Channel = channel };
        Channels.Add(existing);
        return existing;
    }
}

/// <summary>What a reason code is for (PRD §4.5, <c>ReasonCode</c>).</summary>
public enum ReasonCodeKind
{
    Adjustment,
    ShortPick,
    Inspection,
}

/// <summary>An adjustment, short-pick or inspection reason, editable by the host: <c>warehouse-reason-codes/{code}</c>.</summary>
public sealed class ReasonCode
{
    public static string IdFor(string code) => "warehouse-reason-codes/" + WarehouseIds.Normalize(code);

    public string? Id { get; set; }

    public string Code { get; set; } = "";

    public string Name { get; set; } = "";

    public ReasonCodeKind Kind { get; set; }

    public bool Active { get; set; } = true;
}
