using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Warehouse.Models;

namespace MintPlayer.Spark.Warehouse.Outbound.Models;

/// <summary>Where the warehouse is with an outbound order (PRD §4.5 <c>OutboundOrder</c>). States only move forward.</summary>
public enum OutboundState
{
    /// <summary>Accepted at intake; waiting for release (a wave, a supervisor, a hold, <c>ShipNotBefore</c>, or stock for <c>ShipComplete</c>).</summary>
    Accepted,
    /// <summary>Refused at intake; <see cref="OutboundOrder.RejectionReason"/> says why.</summary>
    Rejected,
    /// <summary>Allocated and turned into pick work.</summary>
    Released,
    /// <summary>At least one unit picked.</summary>
    Picking,
    /// <summary>Every allocation picked (and, for put-wall and zone picking, every unit arrived at its slot).</summary>
    Picked,
    /// <summary>A pack station opened a shipment for it.</summary>
    Packing,
    /// <summary>Labelled; waiting for the handover scan (Q-W10).</summary>
    Packed,
    /// <summary>Every line shipped, cancelled, or (CancelRemainder) cancelled as a remainder.</summary>
    Shipped,
    /// <summary>Cancelled before anything shipped.</summary>
    Cancelled,
}

/// <summary>
/// The warehouse's working document for one <c>fulfilment-orders</c> replica (PRD §4.3, §4.5): <c>outbound/{fulfilmentOrderId}</c>
/// (a natural id: a redelivered order is the same document). The warehouse owns it and publishes its state as
/// <see cref="FulfilmentOrderStatus"/> (<c>{fulfilmentOrderId}/status</c>) in the same session as every change.
/// </summary>
public sealed class OutboundOrder : IOrganizationOwned
{
    public const string IdPrefix = "outbound/";

    public static string IdFor(string fulfilmentOrderId) => IdPrefix + fulfilmentOrderId;

    public string? Id { get; set; }

    public string FulfilmentOrderId { get; set; } = "";

    /// <summary>The shop module the order came from (its owner link).</summary>
    public string SourceModule { get; set; } = "";

    /// <summary>The owner as the shop names it; the published status carries it (the row filter keys on it, Q-W23).</summary>
    public string ExternalOwnerId { get; set; } = "";

    /// <summary>The warehouse's owner organization (WD3), or null for a rejected order of an unlinked owner.</summary>
    public string? OrganizationId { get; set; }

    public string Channel { get; set; } = "";

    public string OrderNumber { get; set; } = "";

    public DateTimeOffset PlacedAt { get; set; }

    public string? SiteId { get; set; }

    /// <summary>Why this site (the routing rule, or the owner's default site).</summary>
    public string? RoutingReason { get; set; }

    public OutboundState State { get; set; }

    public FulfilmentRejectionReason? RejectionReason { get; set; }

    public string? RejectionDetail { get; set; }

    public FulfilmentAddress ShipTo { get; set; } = new();

    public string? CarrierCode { get; set; }

    public string? ServiceCode { get; set; }

    public DateOnly? ShipNotBefore { get; set; }

    public bool ShipComplete { get; set; }

    public FulfilmentBackorderPolicy BackorderPolicy { get; set; }

    public int Priority { get; set; }

    /// <summary>The shop's hold codes (a copy of the replica's): never released while any is set.</summary>
    public List<string> Holds { get; set; } = [];

    /// <summary>A supervisor's hold (or a short pick under <c>HoldOrder</c>).</summary>
    public bool ManualHold { get; set; }

    public string? HoldReason { get; set; }

    public List<OutboundLine> Lines { get; set; } = [];

    /// <summary>The wave that released it, or null.</summary>
    public string? WaveId { get; set; }

    /// <summary>The pick batches (<c>WarehouseTaskGroup</c>s) its work is in.</summary>
    public List<string> GroupIds { get; set; } = [];

    /// <summary>How its units are sorted per order after picking (the plan's sortation); put-wall and zone picking need a slot.</summary>
    public PickSortation Sortation { get; set; }

    /// <summary>The consolidation slot or put-wall slot assigned at the first arrival, or null.</summary>
    public string? ConsolidationSlotId { get; set; }

    /// <summary>The shop's cancellation request was seen (the replica's <c>CancellationRequested</c>).</summary>
    public bool CancellationRequested { get; set; }

    public List<string> CancellationLineIds { get; set; } = [];

    /// <summary>The warehouse's answer (WD2 handshake); null while undecided.</summary>
    public FulfilmentCancellationAnswer? CancellationAnswer { get; set; }

    public string? CancellationRefusalReason { get; set; }

    /// <summary>Shipments created so far (<c>{fulfilmentOrderId}/shipments/{n}</c>, n = 1…).</summary>
    public int ShipmentCount { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }

    public DateTimeOffset? ReleasedAt { get; set; }

    public DateTimeOffset? ShippedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Whether a partial shipment is allowed (the shop's <c>ShipComplete</c> off).</summary>
    public bool PartialShipmentAllowed => !ShipComplete;

    public OutboundLine? Line(string lineId) => Lines.FirstOrDefault(l => l.LineId == lineId);

    /// <summary>States in which nothing more happens.</summary>
    public bool IsClosed => State is OutboundState.Rejected or OutboundState.Shipped or OutboundState.Cancelled;
}

/// <summary>A line of an <see cref="OutboundOrder"/>: its allocations, picks and cumulative counters (PRD §4.3).</summary>
public sealed class OutboundLine
{
    public string LineId { get; set; } = "";

    /// <summary>1-based, for the strategies' <c>PickLine.LineNumber</c>.</summary>
    public int Number { get; set; }

    public string Sku { get; set; } = "";

    public string? Name { get; set; }

    public string ItemId { get; set; } = "";

    /// <summary>Base units ordered.</summary>
    public decimal Quantity { get; set; }

    public string? RequiredLotCode { get; set; }

    /// <summary>Claims on balances, picked or not.</summary>
    public List<OutboundAllocation> Allocations { get; set; } = [];

    /// <summary>Units picked into totes and drop locations, with where they are now.</summary>
    public List<OutboundPick> Picks { get; set; } = [];

    /// <summary>The next allocation's sequence: part of its op id, so a retried claim is applied once.</summary>
    public int NextAllocationSeq { get; set; } = 1;

    /// <summary>Units counted in the channel's open quantity of the stock level (§4.4): raised at acceptance, lowered by cancellations.</summary>
    public decimal OpenReported { get; set; }

    public decimal Shipped { get; set; }

    /// <summary>Open, waiting for stock (<see cref="FulfilmentBackorderPolicy.Allow"/>).</summary>
    public decimal Backordered { get; set; }

    /// <summary>Cancelled: by an accepted cancellation, or as a remainder (<see cref="FulfilmentBackorderPolicy.CancelRemainder"/>).</summary>
    public decimal Cancelled { get; set; }

    /// <summary>Units the pickers did not find (summed over short picks).</summary>
    public decimal ShortPicked { get; set; }

    /// <summary>Allocated and not yet picked.</summary>
    public decimal OpenAllocated => Allocations.Sum(a => a.Quantity - a.Picked);

    /// <summary>Picked and still in the warehouse (not shipped, not put back).</summary>
    public decimal PickedOnHand => Picks.Sum(p => p.Quantity - p.Shipped - p.PutBack);

    /// <summary>What still has to be allocated: ordered − shipped − cancelled − backordered − allocated − picked on hand.</summary>
    public decimal Unallocated => Math.Max(0, Quantity - Shipped - Cancelled - Backordered - OpenAllocated - PickedOnHand);

    /// <summary>Units the order still owes the buyer.</summary>
    public decimal Remaining => Math.Max(0, Quantity - Shipped - Cancelled);
}

/// <summary>One claim on a balance (PRD §4.5: "lines with allocations (balance, lot, quantity)").</summary>
public sealed class OutboundAllocation
{
    public int Seq { get; set; }

    public string BalanceId { get; set; } = "";

    public string LocationId { get; set; } = "";

    public string? ZoneId { get; set; }

    public long WalkSequence { get; set; }

    public string? LotCode { get; set; }

    public DateOnly? Expiry { get; set; }

    /// <summary>Units still claimed (released units are subtracted).</summary>
    public decimal Quantity { get; set; }

    /// <summary>Of <see cref="Quantity"/>, units already picked.</summary>
    public decimal Picked { get; set; }

    /// <summary>The pick task that visits it, or null before release made work of it.</summary>
    public string? TaskId { get; set; }
}

/// <summary>Picked units of a line: the balance they now lie on (the drop location, allocation carried), the tote and zone.</summary>
public sealed class OutboundPick
{
    /// <summary>The movement's op id (idempotency anchor).</summary>
    public string OpId { get; set; } = "";

    /// <summary>The balance the units moved to, <c>{dropLocationId}/{itemId}/{lot}</c>; packing ships from it.</summary>
    public string BalanceId { get; set; } = "";

    public string LocationId { get; set; } = "";

    /// <summary>Where they came from (a put-back returns them there).</summary>
    public string FromLocationId { get; set; } = "";

    public string? LotCode { get; set; }

    public DateOnly? Expiry { get; set; }

    public decimal Quantity { get; set; }

    public string? ToteId { get; set; }

    public string? ZoneId { get; set; }

    public string? TaskId { get; set; }

    /// <summary>Units of this pick put into the order's slot (put wall, consolidation station).</summary>
    public decimal Arrived { get; set; }

    public decimal Shipped { get; set; }

    public decimal PutBack { get; set; }

    /// <summary>The put-back task created for this pick by a cancellation, or null.</summary>
    public string? PutBackTaskId { get; set; }
}

/// <summary>
/// A put-wall or consolidation-station slot (PRD §4.5 <c>ConsolidationSlot</c>): <c>{siteId}/consolidation/{slot}</c>. Created
/// by a supervisor; an order takes a free one at its first arrival under optimistic concurrency, so no slot is ever assigned to
/// two orders (W4), and gives it back when shipped or cancelled.
/// </summary>
public sealed class ConsolidationSlot
{
    public static string IdFor(string siteId, string code) => $"{siteId}/consolidation/{WarehouseIds.Normalize(code)}";

    public static string PrefixFor(string siteId) => $"{siteId}/consolidation/";

    public string? Id { get; set; }

    public string SiteId { get; set; } = "";

    public string Code { get; set; } = "";

    /// <summary>The put wall or station the slot belongs to (a free text such as <c>WALL-1</c>).</summary>
    public string? Station { get; set; }

    public bool Active { get; set; } = true;

    /// <summary>The order in the slot, or null when free.</summary>
    public string? OrderId { get; set; }

    public DateTimeOffset? AssignedAt { get; set; }

    /// <summary>Units the order expects in the slot, and units arrived.</summary>
    public decimal Expected { get; set; }

    public decimal Arrived { get; set; }

    /// <summary>Totes (zone totes, batch totes) that delivered into the slot.</summary>
    public List<string> Totes { get; set; } = [];
}

/// <summary>One wave release at one cut-off of a site: <c>outbound-waves/{siteCode}/{cutOffUtc:yyyyMMddHHmm}</c>, created once (natural id).</summary>
public sealed class OutboundWave
{
    public const string IdPrefix = "outbound-waves/";

    public string? Id { get; set; }

    public string SiteId { get; set; } = "";

    public DateTimeOffset CutOff { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public int Released { get; set; }

    public int Waiting { get; set; }
}

/// <summary>The last cut-off a site's wave schedule released: <c>outbound-wave-schedules/{siteCode}</c>.</summary>
public sealed class OutboundWaveSchedule
{
    public const string IdPrefix = "outbound-wave-schedules/";

    public string? Id { get; set; }

    public DateTimeOffset LastCutOff { get; set; }
}
