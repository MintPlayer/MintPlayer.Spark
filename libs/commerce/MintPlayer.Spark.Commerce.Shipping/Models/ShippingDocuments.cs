using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Shipping.Models;

/// <summary>
/// A postal address as carriers take it. Shop maps its reference-data address (commerce §4.18, K8) onto this; Shipping
/// itself only validates the shape (<see cref="ShippingAddressRules"/>), and a host can add the reference data's
/// validation through <see cref="IShippingAddressValidator"/>.
/// </summary>
public sealed class ShippingAddress
{
    public string Name { get; set; } = string.Empty;

    public string? Company { get; set; }

    public string Street { get; set; } = string.Empty;

    public string? HouseNumber { get; set; }

    /// <summary>Box, bus, apartment.</summary>
    public string? Addition { get; set; }

    public string PostalCode { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    /// <summary>ISO 3166-1 alpha-2, upper case.</summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>ISO 3166-2 subdivision (US states, regional holidays), when it matters.</summary>
    public string? Subdivision { get; set; }

    /// <summary>For the carrier's delivery notices; never shown on a query.</summary>
    public string? Email { get; set; }

    public string? Phone { get; set; }
}

/// <summary>
/// One shipment: one or more parcels from one address to another with one carrier service (GB5). Written only by
/// <see cref="ISparkShipping"/>. Labels and customs documents are attachments of this document
/// (<c>label-{n}.pdf</c>, <c>customs.pdf</c>), so they go with it and are never in a query result.
/// </summary>
public class Shipment
{
    public string? Id { get; set; }

    /// <summary>Our reference: the order or fulfilment-order id.</summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>The owner (seller or warehouse client organization) whose carrier account booked it; null for the host's own.</summary>
    public string? Owner { get; set; }

    public ShipmentDirection Direction { get; set; }

    /// <summary>For a return label: the outbound shipment it returns.</summary>
    public string? ReturnOf { get; set; }

    /// <summary>For a return label: why (withdrawal, guarantee claim), as the caller states it.</summary>
    public string? ReturnReason { get; set; }

    public string Carrier { get; set; } = string.Empty;

    public string ServiceCode { get; set; } = string.Empty;

    /// <summary>The configured method (<c>Spark:Commerce:Shipping:Methods:{key}</c>) the buyer chose, when there was one.</summary>
    public string? Method { get; set; }

    /// <summary>The carrier account; null for the default.</summary>
    public string? Account { get; set; }

    public string? CarrierShipmentId { get; set; }

    public ShippingAddress ShipFrom { get; set; } = new();

    public ShippingAddress ShipTo { get; set; } = new();

    /// <summary>The service point or locker (GB5), when delivered to one.</summary>
    public ShipmentServicePoint? ServicePoint { get; set; }

    public List<ShipmentParcel> Parcels { get; set; } = [];

    /// <summary>The customs declaration (GB6), when the shipment leaves the EU customs territory.</summary>
    public CustomsDeclaration? Customs { get; set; }

    /// <summary>The dangerous-goods classes on board (GC9).</summary>
    public List<DangerousGoodsItem> DangerousGoods { get; set; } = [];

    /// <summary>The shipment's status: the least advanced of its live parcels' statuses.</summary>
    public TrackingStatus Status { get; set; } = TrackingStatus.Pending;

    /// <summary>Every live parcel is final (delivered, returned or cancelled): tracking stops. Stored for the poll's query.</summary>
    public bool Final { get; set; }

    /// <summary>Tracking events, deduplicated on their id, in event-time order.</summary>
    public List<TrackingEventRecord> Events { get; set; } = [];

    /// <summary>The delivery promise given when the shipment was booked (GC11).</summary>
    public DateOnly? PromisedFrom { get; set; }

    public DateOnly? PromisedBy { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Who booked it (a user id), or null for an automatic booking.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>The first live parcel's tracking number, for the query.</summary>
    public string? TrackingNumber => Parcels.FirstOrDefault(p => !p.Voided)?.TrackingNumber;

    /// <summary>
    /// Bookings recorded before the carrier was called and not yet answered (carrier idempotency). An entry stays when
    /// the carrier's answer was lost; the next attempt and <c>Shipping.BookingReconcile</c> look it up at the carrier
    /// before booking it again.
    /// </summary>
    public List<PendingBooking> PendingBookings { get; set; } = [];

    /// <summary>A booking is pending; stored for the reconciliation's query.</summary>
    public bool HasPendingBooking { get; set; }
}

/// <summary>
/// One booking recorded before the carrier is called (carrier idempotency): which parcels it covers and the key the
/// carrier saw. Removed once the carrier's labels are stored.
/// </summary>
public sealed class PendingBooking
{
    /// <summary>The booking key without the retry suffix: the shipment id, or <c>{id}/{first parcel number}</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Raised each time the carrier refused an attempt, so the next attempt sends a fresh reference (<c>{Key}-r{n}</c>).</summary>
    public int Generation { get; set; }

    /// <summary>The parcels (<see cref="ShipmentParcel.Number"/>) this booking labels.</summary>
    public List<int> Parcels { get; set; } = [];

    /// <summary>How many times the carrier was called for it.</summary>
    public int Attempts { get; set; }

    /// <summary>The label format asked (kept for a booking resumed later).</summary>
    public LabelFormat LabelFormat { get; set; }

    public PendingBookingState State { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? LastAttemptAtUtc { get; set; }

    /// <summary>The carrier's last message, when it refused or failed.</summary>
    public string? Error { get; set; }

    /// <summary>The reference the carrier saw for the current generation.</summary>
    public string CarrierReference => Generation == 0 ? Key : $"{Key}-r{Generation}";
}

/// <summary>What is known about a pending booking at the carrier.</summary>
public enum PendingBookingState
{
    /// <summary>Recorded, the carrier not called yet: booking it is safe.</summary>
    New,
    /// <summary>The carrier was called and its answer was lost: it may hold the booking. Look it up before booking again.</summary>
    InDoubt,
    /// <summary>The carrier refused it (a 4xx answer): nothing was booked, the next attempt books under a fresh reference.</summary>
    Refused,
    /// <summary>A lookup found nothing at the carrier, or staff confirmed so: booking it again is safe.</summary>
    NotAtCarrier,
}

/// <summary>Outbound to the buyer, or a return label from the buyer (GB5).</summary>
public enum ShipmentDirection
{
    Outbound,
    Return,
}

/// <summary>A shipment's service point (GB5).</summary>
public sealed class ShipmentServicePoint
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public ServicePointKind Kind { get; set; }

    public ShippingAddress Address { get; set; } = new();
}

/// <summary>One parcel of a shipment, with its own label and tracking (GB5).</summary>
public sealed class ShipmentParcel
{
    /// <summary>1-based, stable: the label attachment is <c>label-{Number}</c>.</summary>
    public int Number { get; set; }

    public int WeightGrams { get; set; }

    public int LengthMm { get; set; }

    public int WidthMm { get; set; }

    public int HeightMm { get; set; }

    public string? CarrierParcelId { get; set; }

    /// <summary>The carrier's id of the booking that labelled this parcel (an added parcel is its own booking).</summary>
    public string? CarrierShipmentId { get; set; }

    public string? TrackingNumber { get; set; }

    public string? TrackingUrl { get; set; }

    /// <summary>The attachment name of the label (<c>label-1.pdf</c>, <c>label-1.zpl</c>).</summary>
    public string? LabelAttachment { get; set; }

    public string? LabelMediaType { get; set; }

    public int? LabelDpi { get; set; }

    public TrackingStatus Status { get; set; } = TrackingStatus.Pending;

    /// <summary>The label was voided; the parcel no longer counts.</summary>
    public bool Voided { get; set; }

    /// <summary>What is in the parcel (SKU and quantity), when the packer recorded it.</summary>
    public List<ParcelLine> Lines { get; set; } = [];
}

/// <summary>A SKU and quantity in a parcel.</summary>
public sealed class ParcelLine
{
    public string Sku { get; set; } = string.Empty;

    public int Quantity { get; set; }
}

/// <summary>One recorded tracking event.</summary>
public sealed class TrackingEventRecord
{
    public string EventId { get; set; } = string.Empty;

    public string TrackingNumber { get; set; } = string.Empty;

    public TrackingStatus Status { get; set; }

    public string CarrierStatus { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public DateTime ReceivedAtUtc { get; set; }
}

/// <summary>Our normalized tracking status. The order is the lifecycle order; terminal states are last.</summary>
public enum TrackingStatus
{
    /// <summary>Not booked yet, or the booking failed.</summary>
    Pending,
    /// <summary>The label exists; the carrier has not seen the parcel.</summary>
    Labelled,
    /// <summary>The carrier has the parcel.</summary>
    InTransit,
    OutForDelivery,
    /// <summary>At a service point or locker, waiting for the buyer.</summary>
    AvailableForPickup,
    /// <summary>A delivery problem the carrier is still handling.</summary>
    Exception,
    Delivered,
    /// <summary>Returned to the sender (undeliverable). For a return label, <see cref="Delivered"/> means "received".</summary>
    ReturnedToSender,
    /// <summary>The label was voided or the carrier cancelled the parcel.</summary>
    Cancelled,
}

/// <summary>Helpers over <see cref="TrackingStatus"/>.</summary>
public static class TrackingStatuses
{
    /// <summary>Delivered, returned to sender or cancelled: tracking stops.</summary>
    public static bool IsFinal(this TrackingStatus status)
        => status is TrackingStatus.Delivered or TrackingStatus.ReturnedToSender or TrackingStatus.Cancelled;
}

/// <summary>
/// A customs declaration (GB6). Required for goods leaving the EU customs territory (Regulation (EU) No 952/2013,
/// Union Customs Code, Art. 4 defines that territory), and for postal items to territories inside it but outside the
/// EU VAT territory (the Canary Islands), which need a declaration for fiscal purposes.
/// </summary>
public sealed class CustomsDeclaration
{
    public CustomsDocumentKind Document { get; set; }

    /// <summary>DAP (buyer pays duties) or DDP (seller pays), per destination zone.</summary>
    public Incoterm Incoterm { get; set; } = Incoterm.Dap;

    public CustomsContentType ContentType { get; set; } = CustomsContentType.CommercialGoods;

    /// <summary>The invoice number (required for commercial goods by most carriers).</summary>
    public string? InvoiceNumber { get; set; }

    public List<CustomsItem> Items { get; set; } = [];

    /// <summary>The attachment name of the document, when the carrier returned one separately.</summary>
    public string? DocumentAttachment { get; set; }

    /// <summary>The declared value: the sum of the items.</summary>
    public Money TotalValue(string currency) => Money.Sum(Items.Select(i => i.Value), currency);
}

/// <summary>One declared line (GB6): the HS code and country of origin come from the product or variant (Shop).</summary>
public sealed class CustomsItem
{
    public string Description { get; set; } = string.Empty;

    public int Quantity { get; set; }

    /// <summary>Net weight of all units of the line.</summary>
    public int WeightGrams { get; set; }

    /// <summary>Value of all units of the line.</summary>
    public Money Value { get; set; } = Money.Zero("EUR");

    /// <summary>The Harmonized System code: 6 digits (HS), or 8/10 (CN/TARIC).</summary>
    public string HsCode { get; set; } = string.Empty;

    /// <summary>The country of origin, ISO 3166-1 alpha-2.</summary>
    public string OriginCountry { get; set; } = string.Empty;

    public string? Sku { get; set; }
}

/// <summary>The customs document of a shipment (GB6).</summary>
public enum CustomsDocumentKind
{
    /// <summary>CN22: letter post up to the threshold.</summary>
    Cn22,
    /// <summary>CN23: letter post above the threshold, and every postal parcel.</summary>
    Cn23,
    /// <summary>A commercial invoice (express and courier services).</summary>
    CommercialInvoice,
}

/// <summary>Who pays import duties and taxes (Incoterms® 2020).</summary>
public enum Incoterm
{
    /// <summary>Delivered At Place: the buyer pays duties and import VAT on delivery.</summary>
    Dap,
    /// <summary>Delivered Duty Paid: the seller pays them.</summary>
    Ddp,
}

/// <summary>The nature of the goods on a customs form.</summary>
public enum CustomsContentType
{
    Gift,
    Documents,
    CommercialGoods,
    CommercialSample,
    ReturnedGoods,
}

/// <summary>
/// The dangerous-goods classes a webshop or warehouse item can carry (GC9): the lithium-battery entries of the UN
/// Model Regulations and IATA DGR packing instructions, ADR limited and excepted quantities, and "other" for any
/// other UN number. A carrier service that does not accept a class is never offered for a parcel that carries it.
/// </summary>
public enum DangerousGoodsClass
{
    /// <summary>UN3481, lithium-ion batteries contained in equipment (IATA PI 967).</summary>
    LithiumIonInEquipment,
    /// <summary>UN3481, lithium-ion batteries packed with equipment (IATA PI 966).</summary>
    LithiumIonPackedWithEquipment,
    /// <summary>UN3480, lithium-ion batteries on their own (IATA PI 965).</summary>
    LithiumIonBatteries,
    /// <summary>UN3091, lithium-metal batteries contained in equipment (IATA PI 970).</summary>
    LithiumMetalInEquipment,
    /// <summary>UN3091, lithium-metal batteries packed with equipment (IATA PI 969).</summary>
    LithiumMetalPackedWithEquipment,
    /// <summary>UN3090, lithium-metal batteries on their own (IATA PI 968).</summary>
    LithiumMetalBatteries,
    /// <summary>ADR limited quantities (ADR 3.4).</summary>
    LimitedQuantity,
    /// <summary>ADR excepted quantities (ADR 3.5).</summary>
    ExceptedQuantity,
    /// <summary>Any other UN number: never accepted unless a service's configuration says so.</summary>
    Other,
}

/// <summary>A dangerous-goods entry of a parcel (GC9).</summary>
public sealed class DangerousGoodsItem
{
    public DangerousGoodsClass Class { get; set; }

    /// <summary>The UN number (<c>UN3481</c>); defaults from <see cref="Class"/> for the lithium classes.</summary>
    public string? UnNumber { get; set; }
}
