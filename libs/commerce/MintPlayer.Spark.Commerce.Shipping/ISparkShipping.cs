using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Shipping.Models;

namespace MintPlayer.Spark.Commerce.Shipping;

/// <summary>
/// Shipping (commerce PRD §4.9): rates, service points, labels, return labels, voids and tracking. Shop's checkout
/// step and its staff screens, and the warehouse pack station, call this; neither talks to a carrier adapter
/// directly. Every booking is checked against the carrier's capability catalog: a service that does not reach the
/// destination, carry the weight or accept the parcel's dangerous goods (GC9) is refused, and a shipment that leaves
/// the EU customs territory is refused without complete customs lines (GB6).
/// </summary>
public interface ISparkShipping
{
    /// <summary>The registered carriers.</summary>
    IReadOnlyList<IShippingCarrier> Carriers { get; }

    /// <summary>
    /// The rates a basket may be offered (every <see cref="IShippingRateProvider"/>, configuration first), each
    /// checked against its carrier's catalog, with the customs requirement and Incoterm of the destination zone (GB6)
    /// and the delivery promise (GC11). Sorted by the method's order, then price.
    /// </summary>
    Task<IReadOnlyList<ShippingRate>> GetRatesAsync(ShippingRateRequest request, CancellationToken cancellationToken);

    /// <summary>Service points and lockers near an address (GB5), cached for <see cref="SparkCommerceShippingOptions.ServicePointCacheDuration"/>.</summary>
    Task<IReadOnlyList<ServicePoint>> SearchServicePointsAsync(string? carrier, ServicePointQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Books a shipment with one label per parcel (GB5). With <see cref="CreateShipmentRequest.IdempotencyKey"/>, a
    /// second call answers the first call's shipment. Throws <see cref="ShippingRequestException"/> for a request the
    /// catalog refuses and <see cref="CarrierException"/> when the carrier refuses.
    /// </summary>
    Task<Shipment> CreateShipmentAsync(CreateShipmentRequest request, CancellationToken cancellationToken);

    /// <summary>Adds a parcel, with its own label and tracking, to a shipment (GB5, shop-only multi-parcel).</summary>
    Task<Shipment> AddParcelAsync(string shipmentId, ParcelRequest parcel, string? userId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a return label for an outbound shipment (GB5): from the buyer back to
    /// <see cref="SparkCommerceShippingOptions.ReturnAddress"/>. The <see cref="IShipmentEventRecipient"/>s hear of it
    /// (Shop mails it to the buyer).
    /// </summary>
    Task<Shipment> CreateReturnLabelAsync(ReturnLabelRequest request, CancellationToken cancellationToken);

    /// <summary>Voids one parcel's label, or every parcel's when <paramref name="parcelNumber"/> is null. Answers whether all asked were voided.</summary>
    Task<bool> VoidAsync(string shipmentId, int? parcelNumber, string? userId, CancellationToken cancellationToken);

    /// <summary>A parcel's label: its bytes and media type, or null.</summary>
    Task<CarrierDocument?> GetLabelAsync(string shipmentId, int parcelNumber, CancellationToken cancellationToken);

    /// <summary>The customs document, when the carrier returned one separately (GB6).</summary>
    Task<CarrierDocument?> GetCustomsDocumentAsync(string shipmentId, CancellationToken cancellationToken);

    /// <summary>
    /// Records tracking events of <paramref name="carrier"/> (from its webhook or the poll): deduplicated on the event
    /// id, ordered by event time; events of parcels we never labelled are dropped. Answers how many were new.
    /// </summary>
    Task<int> RecordTrackingAsync(string carrier, IReadOnlyList<CarrierTrackingEvent> events, CancellationToken cancellationToken);

    /// <summary>Polls the carriers that push no webhooks for the parcels still moving.</summary>
    Task PollTrackingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reconciles bookings whose carrier answer was lost (<see cref="PendingBookingState.InDoubt"/>, older than
    /// <see cref="SparkCommerceShippingOptions.BookingReconcileAfter"/>): each is looked up at the carrier
    /// (<see cref="IShippingBookingLookup"/>) and, when found, its labels are stored as if the answer had arrived; when
    /// not found it becomes safe to book again. Never books. Run by <c>Shipping.BookingReconcile</c>. Answers how many
    /// bookings were resolved.
    /// </summary>
    Task<int> ReconcilePendingBookingsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Staff confirm that the carrier holds none of a shipment's in-doubt bookings (checked in the carrier's own
    /// panel), for carriers that cannot look a booking up (PostNL, Sendcloud returns): the next attempt books again.
    /// Answers false when the shipment has no in-doubt booking.
    /// </summary>
    Task<bool> ReleasePendingBookingAsync(string shipmentId, string? userId, CancellationToken cancellationToken);
}

/// <summary>
/// A booking may exist at the carrier but cannot be confirmed (its answer was lost and the carrier cannot be asked).
/// Booking again could pay twice: staff check the carrier's panel and then call
/// <see cref="ISparkShipping.ReleasePendingBookingAsync"/>, or void it there.
/// </summary>
public sealed class ShipmentBookingInDoubtException(string shipmentId, string carrier)
    : Exception($"Shipment {shipmentId}: an earlier {carrier} booking may exist at the carrier and cannot be looked up. Check the carrier's panel, then release the pending booking to book again.")
{
    public string ShipmentId { get; } = shipmentId;

    public string Carrier { get; } = carrier;
}

/// <summary>What a rate is asked for.</summary>
public sealed record ShippingRateRequest
{
    public required ShippingAddress ShipTo { get; init; }

    /// <summary>The ship-from address; <see cref="SparkCommerceShippingOptions.Sender"/> when null.</summary>
    public ShippingAddress? ShipFrom { get; init; }

    /// <summary>The goods subtotal, for the free-shipping threshold (in <see cref="SparkCommerceShippingOptions.Currency"/>).</summary>
    public required Money Subtotal { get; init; }

    /// <summary>The estimated gross weight.</summary>
    public int WeightGrams { get; init; }

    /// <summary>The dangerous-goods classes in the basket (GC9).</summary>
    public IReadOnlyList<DangerousGoodsItem> DangerousGoods { get; init; } = [];

    /// <summary>The owner (seller) whose methods apply; providers may use it.</summary>
    public string? Owner { get; init; }

    /// <summary>Compute the delivery promise (GC11) as of this instant; now when null.</summary>
    public DateTimeOffset? At { get; init; }
}

/// <summary>One offerable rate.</summary>
public sealed record ShippingRate
{
    /// <summary>The method key (<c>Spark:Commerce:Shipping:Methods:{key}</c>), or the provider's own key.</summary>
    public required string Method { get; init; }

    public ShippingMethodKind Kind { get; init; }

    public string? Carrier { get; init; }

    public string? ServiceCode { get; init; }

    public required Money Price { get; init; }

    /// <summary>The free-shipping threshold applied.</summary>
    public bool Free { get; init; }

    /// <summary>The goods subtotal from which this method is free, when it has a threshold in the zone.</summary>
    public Money? FreeFrom { get; init; }

    public string? Zone { get; init; }

    /// <summary>The service delivers to a service point or locker (GB5): checkout shows the point picker.</summary>
    public bool ServicePoint { get; init; }

    /// <summary>Why customs apply (GB6); <see cref="CustomsReason.None"/> inside the EU customs and VAT territory.</summary>
    public CustomsReason Customs { get; init; }

    /// <summary>DAP or DDP, shown to the buyer when customs apply (GB6).</summary>
    public Incoterm? Incoterm { get; init; }

    /// <summary>The delivery promise (GC11), when the method states transit days.</summary>
    public DeliveryPromise? Promise { get; init; }

    /// <summary>Where to collect, for pickup.</summary>
    public string? PickupLocation { get; init; }

    public int Order { get; init; }
}

/// <summary>
/// A source of rates. Configuration's methods are one (built in); a carrier's live price API or a host's own table
/// are others. Discovered in an application (<c>[SparkDiscoverable]</c>). A provider answers raw rates; Shipping
/// checks them against the carrier catalog.
/// </summary>
[SparkDiscoverable]
public interface IShippingRateProvider
{
    Task<IReadOnlyList<ShippingRate>> GetRatesAsync(ShippingRateRequest request, CancellationToken cancellationToken);
}

/// <summary>One shipment to book.</summary>
public sealed record CreateShipmentRequest
{
    /// <summary>The order or fulfilment-order id.</summary>
    public required string Reference { get; init; }

    public string? Owner { get; init; }

    /// <summary>The configured method the buyer chose; it names the carrier and service.</summary>
    public string? Method { get; init; }

    /// <summary>The carrier, when no method names it; the default carrier when both are null.</summary>
    public string? Carrier { get; init; }

    /// <summary>The service, when no method names it.</summary>
    public string? ServiceCode { get; init; }

    /// <summary><see cref="SparkCommerceShippingOptions.Sender"/> when null (a warehouse site passes its own).</summary>
    public ShippingAddress? ShipFrom { get; init; }

    public required ShippingAddress ShipTo { get; init; }

    /// <summary>The service point or locker the buyer chose (GB5).</summary>
    public ServicePoint? ServicePoint { get; init; }

    public required IReadOnlyList<ParcelRequest> Parcels { get; init; }

    /// <summary>The customs lines (GB6): required when the shipment leaves the EU customs territory.</summary>
    public IReadOnlyList<CustomsItem>? CustomsItems { get; init; }

    public CustomsContentType? CustomsContentType { get; init; }

    public string? InvoiceNumber { get; init; }

    /// <summary>The dangerous-goods classes on board (GC9).</summary>
    public IReadOnlyList<DangerousGoodsItem>? DangerousGoods { get; init; }

    /// <summary>The carrier account (per owner, warehouse WD8); the default when null.</summary>
    public string? Account { get; init; }

    public LabelFormat? LabelFormat { get; init; }

    /// <summary>
    /// Makes the call retry-safe: the shipment's id is derived from it, so a second call answers the first call's
    /// shipment instead of booking again. Letters, digits, <c>-</c>, <c>_</c> and <c>/</c>; at most 120.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    public string? CreatedBy { get; init; }
}

/// <summary>One parcel to label.</summary>
public sealed record ParcelRequest(int WeightGrams, int LengthMm = 0, int WidthMm = 0, int HeightMm = 0)
{
    public IReadOnlyList<ParcelLine> Lines { get; init; } = [];
}

/// <summary>A return label for an outbound shipment (GB5).</summary>
public sealed record ReturnLabelRequest
{
    public required string ShipmentId { get; init; }

    /// <summary>Why: <c>withdrawal</c>, <c>guarantee</c>, … (Shop's return reasons, GA8).</summary>
    public string? Reason { get; init; }

    /// <summary>The return service; the carrier's first return service that serves the buyer's country when null.</summary>
    public string? ServiceCode { get; init; }

    /// <summary>The parcels; one parcel of the outbound's first parcel's weight when null.</summary>
    public IReadOnlyList<ParcelRequest>? Parcels { get; init; }

    public string? IdempotencyKey { get; init; }

    public string? CreatedBy { get; init; }
}

/// <summary>A request the carrier catalog or the rules refuse; <see cref="Problems"/> says why.</summary>
public sealed class ShippingRequestException(IReadOnlyList<string> problems)
    : Exception("The shipment cannot be booked:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// Hears what happened to shipments (Shop: order Shipped/Delivered, the return label mail, refund on receipt or proof
/// of dispatch, GA1; Fulfilment: the shipment replica). Discovered in an application (<c>[SparkDiscoverable]</c>).
/// Called after the change is stored; tracking notices come from the webhook queue or the poll, so a recipient that
/// throws makes the delivery retry: every method must be idempotent.
/// </summary>
[SparkDiscoverable]
public interface IShipmentEventRecipient
{
    /// <summary>A shipment's labels exist.</summary>
    Task OnShipmentLabelledAsync(Shipment shipment, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>A return label exists (GB5): mail it to the buyer.</summary>
    Task OnReturnLabelCreatedAsync(Shipment returnShipment, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>A shipment's status changed.</summary>
    Task OnTrackingChangedAsync(ShipmentTrackingNotice notice, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A shipment's status changed from <see cref="PreviousStatus"/>. <see cref="ProofOfDispatch"/> is set the first time
/// a return shipment is in the carrier's hands: the carrier's scan is the buyer's proof of dispatch, which starts the
/// refund of a withdrawal "on receipt or proof of dispatch, whichever is earlier" (GA1, CRD Art. 13(3)).
/// </summary>
public sealed record ShipmentTrackingNotice(Shipment Shipment, TrackingStatus PreviousStatus, bool ProofOfDispatch);

/// <summary>
/// Narrows the shipments a caller sees (a seller's staff see their own <see cref="Shipment.Owner"/>'s). Discovered in
/// an application (<c>[SparkDiscoverable]</c>); without one, every member of the <c>commerce-shipping:staff</c> slot
/// sees every shipment. Several filters combine with AND.
/// </summary>
[SparkDiscoverable]
public interface IShipmentRowFilter
{
    Task<Expression<Func<Shipment, bool>>?> GetFilterAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Validates a destination before booking (the reference data's address validation, commerce §4.18, is the intended
/// implementation). Optional and discovered (<c>[SparkDiscoverable]</c>); Shipping checks the shape only.
/// </summary>
[SparkDiscoverable]
public interface IShippingAddressValidator
{
    Task<IReadOnlyList<string>> ValidateAsync(ShippingAddress address, string role, CancellationToken cancellationToken);
}
