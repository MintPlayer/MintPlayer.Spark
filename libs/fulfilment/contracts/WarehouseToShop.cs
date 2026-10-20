using MintPlayer.Spark.Replication.Abstractions;

namespace MintPlayer.Spark.Fulfilment.Contracts;

/// <summary>Where the warehouse is with an order (PRD §4.3, <c>fulfilment-status</c>).</summary>
public enum FulfilmentOrderState
{
    Accepted,
    /// <summary>Refused at intake; <see cref="FulfilmentOrderStatus.RejectionReason"/> says why.</summary>
    Rejected,
    Picking,
    Picked,
    Packed,
    /// <summary>Every line shipped, backordered-and-cancelled, or cancelled; see the line quantities.</summary>
    Shipped,
    /// <summary>The whole order was cancelled (the cancellation was accepted before anything shipped).</summary>
    Cancelled,
}

/// <summary>Why the warehouse refused an order at intake (PRD §4.3, Intake).</summary>
public enum FulfilmentRejectionReason
{
    UnknownSku,
    /// <summary>No owner link maps the order's owner (Q-W23).</summary>
    OwnerNotServed,
    InvalidAddress,
    Other,
}

/// <summary>The warehouse's answer to <see cref="FulfilmentOrder.CancellationRequested"/> (WD2 handshake).</summary>
public enum FulfilmentCancellationAnswer
{
    Accepted,
    /// <summary>Refused, e.g. "already picked up by the courier"; the shop turns it into a return.</summary>
    Refused,
}

/// <summary>
/// <c>fulfilment-status</c> (warehouse PRD §4.3): the warehouse's state of one <see cref="FulfilmentOrder"/>. Owner: the
/// warehouse. Id <c>{fulfilmentOrderId}/status</c>, so a redelivery is a no-op.
/// </summary>
/// <remarks>
/// Quantities are cumulative, never deltas, and states only move forward: a late "packed" after "shipped" changes
/// nothing at the shop.
/// </remarks>
[Replicated(SourceCollection = FulfilmentCollections.OrderStatuses)]
public partial class FulfilmentOrderStatus
{
    public string? Id { get; set; }
    public string FulfilmentOrderId { get; set; } = "";
    /// <summary>The owner of the goods, as the order named it (a per-consumer filter would key on it, Q-W23).</summary>
    public string OwnerId { get; set; } = "";
    public FulfilmentOrderState State { get; set; }
    public FulfilmentRejectionReason? RejectionReason { get; set; }
    /// <summary>A text for people beside the reason: the unknown SKU, the address field.</summary>
    public string? RejectionDetail { get; set; }
    public List<FulfilmentOrderLineStatus> Lines { get; set; } = [];
    /// <summary>Null until the shop requests a cancellation and the warehouse decided.</summary>
    public FulfilmentCancellationAnswer? Cancellation { get; set; }
    public string? CancellationRefusalReason { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Per line of a <see cref="FulfilmentOrderStatus"/>: cumulative quantities.</summary>
public sealed class FulfilmentOrderLineStatus
{
    public string LineId { get; set; } = "";
    public decimal Shipped { get; set; }
    /// <summary>Open, waiting for stock (<see cref="FulfilmentBackorderPolicy.Allow"/>).</summary>
    public decimal Backordered { get; set; }
    /// <summary>Cancelled by an accepted cancellation, or as a remainder (<see cref="FulfilmentBackorderPolicy.CancelRemainder"/>).</summary>
    public decimal Cancelled { get; set; }
}

/// <summary>A carrier's tracking state of a shipment, normalised across adapters.</summary>
public enum FulfilmentTrackingState
{
    Unknown,
    LabelCreated,
    InTransit,
    OutForDelivery,
    Delivered,
    /// <summary>A delivery problem the carrier reported.</summary>
    Exception,
    ReturnedToSender,
}

/// <summary>
/// <c>shipments</c> (warehouse PRD §4.3): one parcel set that left the warehouse for an order. Owner: the warehouse. Id
/// <c>{fulfilmentOrderId}/shipments/{n}</c>; an order can produce several.
/// </summary>
[Replicated(SourceCollection = FulfilmentCollections.Shipments)]
public partial class FulfilmentShipment
{
    public string? Id { get; set; }
    public string FulfilmentOrderId { get; set; } = "";
    /// <summary>The owner of the goods, as the order named it (a per-consumer filter would key on it, Q-W23).</summary>
    public string OwnerId { get; set; } = "";
    public List<FulfilmentShipmentLine> Lines { get; set; } = [];
    public List<FulfilmentPackage> Packages { get; set; } = [];
    public string? CarrierCode { get; set; }
    public string? ServiceCode { get; set; }
    /// <summary>The ship-confirm instant (handover scan, or pack-complete when there is none, Q-W10).</summary>
    public DateTimeOffset? ShippedAt { get; set; }
    public FulfilmentTrackingState TrackingState { get; set; }
}

/// <summary>A shipped line, with its lots and serials (GA7: the recall trace lot → shipment → buyer).</summary>
public sealed class FulfilmentShipmentLine
{
    /// <summary>The <see cref="FulfilmentOrderLine.LineId"/>.</summary>
    public string LineId { get; set; } = "";
    public string Sku { get; set; } = "";
    public decimal Quantity { get; set; }
    public List<FulfilmentShippedLot> Lots { get; set; } = [];
    /// <summary>Serials captured at packing (they are not tracked as stock).</summary>
    public List<string> Serials { get; set; } = [];
}

/// <summary>The quantity of a line that came from one lot.</summary>
public sealed class FulfilmentShippedLot
{
    public string LotCode { get; set; } = "";
    public DateOnly? Expiry { get; set; }
    public decimal Quantity { get; set; }
}

/// <summary>One parcel of a shipment. Lengths are millimetres and weights grams.</summary>
public sealed class FulfilmentPackage
{
    /// <summary>The carton type used, by the warehouse's code, or null.</summary>
    public string? Carton { get; set; }
    public decimal? WeightGrams { get; set; }
    public int? LengthMm { get; set; }
    public int? WidthMm { get; set; }
    public int? HeightMm { get; set; }
    /// <summary>The parcel's SSCC (AI 00), when the warehouse allocates them.</summary>
    public string? Sscc { get; set; }
    public string? TrackingNumber { get; set; }
    public string? TrackingUrl { get; set; }
    /// <summary>The carrier's label or parcel id, for a void or a reprint at the carrier.</summary>
    public string? LabelReference { get; set; }
}

/// <summary>
/// <c>stock-levels</c> (warehouse PRD §4.4): what the shop may sell of one SKU. Owner: the warehouse, one document per
/// (owner, SKU), written in the same transaction as the movement that changed it (WI6).
/// </summary>
/// <remarks>
/// The shop computes <c>capacity = OnHandSellable − ReservedOtherChannels − buffer + ShippedTotal</c> for its channel.
/// A snapshot with a lower <see cref="Version"/> than the one held is ignored.
/// </remarks>
[Replicated(SourceCollection = FulfilmentCollections.StockLevels)]
public partial class FulfilmentStockLevel
{
    public string? Id { get; set; }
    public string OwnerId { get; set; } = "";
    public string Sku { get; set; } = "";
    /// <summary>Physical stock in sellable status (Q-W5), across the owner's sites.</summary>
    public decimal OnHandSellable { get; set; }
    public List<FulfilmentChannelStock> Channels { get; set; } = [];
    /// <summary>Monotonic per document.</summary>
    public long Version { get; set; }
}

/// <summary>One channel's view of a <see cref="FulfilmentStockLevel"/>.</summary>
public sealed class FulfilmentChannelStock
{
    public string Channel { get; set; } = "";
    /// <summary>Accepted, unshipped quantity of the OTHER channels' orders.</summary>
    public decimal ReservedOtherChannels { get; set; }
    /// <summary>Cumulative units shipped for this channel; monotonic, sharing an epoch with the shop's <c>sold</c>.</summary>
    public decimal ShippedTotal { get; set; }
}

/// <summary>
/// <c>return-receipts</c> (warehouse PRD §4.3, Returns step 5): what arrived back and what the warehouse did with each
/// line. Owner: the warehouse. For a withdrawal the shop may refund before this arrives (commerce GA1).
/// </summary>
[Replicated(SourceCollection = FulfilmentCollections.ReturnReceipts)]
public partial class FulfilmentReturnReceipt
{
    public string? Id { get; set; }
    /// <summary>The <see cref="FulfilmentExpectedReturn"/> received, or null for a blind return.</summary>
    public string? ExpectedReturnId { get; set; }
    /// <summary>The order the goods belong to, when known.</summary>
    public string? FulfilmentOrderId { get; set; }
    /// <summary>The owner of the goods (a per-consumer filter would key on it, Q-W23).</summary>
    public string OwnerId { get; set; } = "";
    public List<FulfilmentReturnReceiptLine> Lines { get; set; } = [];
    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>A received return line and its disposition.</summary>
public sealed class FulfilmentReturnReceiptLine
{
    /// <summary>The <see cref="FulfilmentExpectedReturnLine.LineId"/>, or null for a blind line.</summary>
    public string? ReturnLineId { get; set; }
    public string Sku { get; set; } = "";
    public decimal Quantity { get; set; }
    public FulfilmentReturnDisposition Disposition { get; set; }
    /// <summary>The inspection's reason code when it overrode the reason's default disposition.</summary>
    public string? DispositionReason { get; set; }
}
