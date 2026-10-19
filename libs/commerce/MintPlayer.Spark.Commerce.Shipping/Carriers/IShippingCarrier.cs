using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Shipping;

/// <summary>
/// One carrier integration (commerce PRD §4.9, D14): Sendcloud, bpost, PostNL or the in-process test carrier. An
/// adapter package registers one implementation with
/// <see cref="SparkCommerceShippingExtensions.AddShippingCarrier{TCarrier}"/>; the rest of the suite (Shop, the
/// warehouse pack station) only talks to <see cref="ISparkShipping"/>, which checks every request against the
/// adapter's capability catalog before it is sent.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written against documented APIs</b> (D37, round 4): every adapter reads its credentials from configuration
/// (<c>Spark:Commerce:Shipping:{Carrier}</c>, user-secrets) and is tested against fixtures taken from the carrier's
/// public documentation. No test calls a carrier.
/// </para>
/// <para>
/// <b>Formats (warehouse §4.12).</b> A label is <c>(bytes, media type)</c>: PDF, or ZPL at a stated dpi. ZPL goes
/// to thermal printers through Printing; PDF goes to the print dialog.
/// </para>
/// </remarks>
public interface IShippingCarrier
{
    /// <summary>The carrier's name, lower case: <c>sendcloud</c>, <c>bpost</c>, <c>postnl</c>, <c>test</c>. Also its webhook source's stem.</summary>
    string Name { get; }

    /// <summary>What the integration can do (the capability catalog).</summary>
    CarrierCapabilities Capabilities { get; }

    /// <summary>
    /// The services the account can book, with the destinations, weights and dangerous-goods classes each accepts
    /// (GC9). <see cref="ISparkShipping"/> never offers or books a service outside these.
    /// </summary>
    IReadOnlyList<CarrierService> Services { get; }

    /// <summary>
    /// Creates the shipment at the carrier with one label per parcel (GB5 multi-parcel) and, when
    /// <see cref="CarrierShipmentRequest.Customs"/> is set, the customs document (GB6). Throws
    /// <see cref="CarrierException"/> with the carrier's message when the carrier refuses.
    /// </summary>
    Task<CarrierShipmentResult> CreateShipmentAsync(CarrierShipmentRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Voids (cancels) a parcel's label before handover. Answers <see langword="false"/> when the carrier no longer
    /// allows it (already scanned) or the integration cannot (<see cref="CarrierCapabilities.VoidLabel"/>). Cancelling
    /// a packed order depends on it (warehouse §4.12).
    /// </summary>
    Task<bool> VoidAsync(CarrierParcelReference parcel, CancellationToken cancellationToken);

    /// <summary>
    /// The parcel's tracking history as the carrier has it now, for carriers that push no webhooks
    /// (<see cref="CarrierCapabilities.TrackingPull"/>). Others may answer an empty list.
    /// </summary>
    Task<IReadOnlyList<CarrierTrackingEvent>> GetTrackingAsync(CarrierParcelReference parcel, CancellationToken cancellationToken);

    /// <summary>
    /// Turns one verified webhook delivery (X5, source <c>shipping-{name}</c>) into tracking events. Only called for
    /// carriers with <see cref="CarrierCapabilities.TrackingWebhooks"/>. An unknown parcel answers its events anyway:
    /// <see cref="ISparkShipping"/> drops events of parcels it never labelled.
    /// </summary>
    Task<IReadOnlyList<CarrierTrackingEvent>> ParseWebhookAsync(WebhookDelivery delivery, CancellationToken cancellationToken);
}

/// <summary>
/// Searches a carrier's service points and parcel lockers near an address (GB5): bpost Pick-up points and
/// parcel lockers, PostNL locations, Sendcloud's service-point API. The chosen point's id travels on the order and
/// the label (<see cref="CarrierShipmentRequest.ServicePointId"/>).
/// </summary>
public interface IServicePointProvider
{
    /// <summary>The carrier this provider searches for (<see cref="IShippingCarrier.Name"/>).</summary>
    string Carrier { get; }

    /// <summary>The points nearest to <paramref name="query"/>, nearest first, at most <see cref="ServicePointQuery.Limit"/>.</summary>
    Task<IReadOnlyList<ServicePoint>> SearchAsync(ServicePointQuery query, CancellationToken cancellationToken);
}

/// <summary>What a carrier integration supports (the capability catalog, §4.9).</summary>
public sealed record CarrierCapabilities
{
    /// <summary>The label formats the carrier returns.</summary>
    public IReadOnlyList<LabelFormat> LabelFormats { get; init; } = [LabelFormat.Pdf];

    /// <summary>A parcel's label can be voided before handover.</summary>
    public bool VoidLabel { get; init; }

    /// <summary>Return labels (GB5): a shipment from the buyer back to the seller.</summary>
    public bool ReturnLabels { get; init; }

    /// <summary>Several parcels in one shipment, each with its own label and tracking (GB5).</summary>
    public bool MultiParcel { get; init; }

    /// <summary>Delivery to a service point or parcel locker (GB5).</summary>
    public bool ServicePoints { get; init; }

    /// <summary>Customs documents (CN22/CN23 or commercial invoice) are created through the API (GB6).</summary>
    public bool CustomsDocuments { get; init; }

    /// <summary>Dangerous-goods marks can be passed to the label through the API (GC9).</summary>
    public bool DangerousGoodsMarks { get; init; }

    /// <summary>The carrier pushes tracking webhooks (received through X5).</summary>
    public bool TrackingWebhooks { get; init; }

    /// <summary>The carrier's tracking can be read by parcel (polled by <c>Shipping.TrackingPoll</c>).</summary>
    public bool TrackingPull { get; init; }
}

/// <summary>A label's format.</summary>
public enum LabelFormat
{
    /// <summary>A PDF (A6 or A4), for the print dialog.</summary>
    Pdf,
    /// <summary>ZPL for thermal printers, at <see cref="CarrierDocument.Dpi"/>.</summary>
    Zpl,
    /// <summary>A PNG image.</summary>
    Png,
}

/// <summary>
/// One bookable carrier service (product): <c>bpack 24h Pro</c>, PostNL <c>3085</c>, a Sendcloud shipping method.
/// </summary>
/// <param name="Code">The carrier's own code, sent when booking.</param>
/// <param name="Name">A display name.</param>
public sealed record CarrierService(string Code, string Name)
{
    /// <summary>Destination countries (ISO 3166-1 alpha-2, upper case). Empty: any destination.</summary>
    public IReadOnlyList<string> Countries { get; init; } = [];

    /// <summary>Origin countries the service ships from. Empty: any.</summary>
    public IReadOnlyList<string> FromCountries { get; init; } = [];

    /// <summary>The lightest parcel, in grams.</summary>
    public int MinWeightGrams { get; init; }

    /// <summary>The heaviest parcel, in grams; 0 for no stated limit.</summary>
    public int MaxWeightGrams { get; init; }

    /// <summary>The service delivers to a service point or parcel locker (GB5), which the booking must name.</summary>
    public bool ServicePoint { get; init; }

    /// <summary>The service is a return service (GB5).</summary>
    public bool Return { get; init; }

    /// <summary>
    /// The dangerous-goods classes the service accepts (GC9). Empty: none, so a parcel with any class is never offered
    /// this service. Carrier rules depend on the host's contract, so adapters ship an empty set and read the accepted
    /// classes from configuration (<c>Services:{code}:AcceptedDangerousGoods</c>).
    /// </summary>
    public IReadOnlyList<DangerousGoodsClass> AcceptedDangerousGoods { get; init; } = [];

    /// <summary>How the service documents goods leaving the EU customs territory (GB6).</summary>
    public CustomsDocumentStyle CustomsStyle { get; init; } = CustomsDocumentStyle.PostalParcel;
}

/// <summary>Which customs document a service uses (GB6).</summary>
public enum CustomsDocumentStyle
{
    /// <summary>Letter post: CN22 up to the threshold, CN23 above it (UPU).</summary>
    LetterPost,
    /// <summary>A postal parcel: always CN23 (UPU).</summary>
    PostalParcel,
    /// <summary>Express or courier: a commercial invoice.</summary>
    CommercialInvoice,
}

/// <summary>One shipment to book (<see cref="IShippingCarrier.CreateShipmentAsync"/>).</summary>
public sealed record CarrierShipmentRequest
{
    /// <summary>Our reference: the order or fulfilment-order id; printed where the carrier allows.</summary>
    public required string Reference { get; init; }

    /// <summary>Our shipment id; adapters that take an idempotency or external-reference key send it.</summary>
    public required string ShipmentId { get; init; }

    /// <summary>The service's <see cref="CarrierService.Code"/>.</summary>
    public required string ServiceCode { get; init; }

    public required ShippingAddress ShipFrom { get; init; }

    public required ShippingAddress ShipTo { get; init; }

    /// <summary>The carrier's service-point or locker id (GB5), when the service delivers to one.</summary>
    public string? ServicePointId { get; init; }

    /// <summary>The service point's name and address, for carriers whose booking repeats them (bpost <c>pugoAddress</c>, PostNL address type 09).</summary>
    public ShipmentServicePoint? ServicePoint { get; init; }

    /// <summary>One entry per parcel (GB5). At least one.</summary>
    public required IReadOnlyList<ParcelSpec> Parcels { get; init; }

    /// <summary>A return shipment (GB5): <see cref="ShipFrom"/> is the buyer.</summary>
    public bool IsReturn { get; init; }

    /// <summary>The customs declaration (GB6) when the shipment leaves the EU customs territory.</summary>
    public CustomsDeclaration? Customs { get; init; }

    /// <summary>The dangerous-goods classes in the shipment (GC9) and the marks the label must carry.</summary>
    public IReadOnlyList<DangerousGoodsItem> DangerousGoods { get; init; } = [];

    /// <summary>The preferred label format; an adapter falls back to PDF when the carrier cannot.</summary>
    public LabelFormat LabelFormat { get; init; } = LabelFormat.Pdf;

    /// <summary>The carrier account (warehouse WD8: per owner, or the default); null for the default.</summary>
    public string? Account { get; init; }
}

/// <summary>One parcel's physical data.</summary>
/// <param name="WeightGrams">The gross weight.</param>
/// <param name="LengthMm">Length, 0 when unknown.</param>
/// <param name="WidthMm">Width, 0 when unknown.</param>
/// <param name="HeightMm">Height, 0 when unknown.</param>
public sealed record ParcelSpec(int WeightGrams, int LengthMm = 0, int WidthMm = 0, int HeightMm = 0);

/// <summary>What the carrier answered for a booking.</summary>
/// <param name="CarrierShipmentId">The carrier's id of the whole shipment (the first parcel's when it has none).</param>
/// <param name="Parcels">One per requested parcel, in the same order.</param>
/// <param name="CustomsDocument">The CN22/CN23 or commercial invoice, when the carrier returns it separately.</param>
public sealed record CarrierShipmentResult(string CarrierShipmentId, IReadOnlyList<CarrierParcelResult> Parcels, CarrierDocument? CustomsDocument = null);

/// <summary>One labelled parcel.</summary>
public sealed record CarrierParcelResult(string CarrierParcelId, string TrackingNumber, string? TrackingUrl, CarrierDocument Label);

/// <summary>A document from the carrier: a label or a customs form.</summary>
/// <param name="Content">The bytes.</param>
/// <param name="MediaType"><c>application/pdf</c>, <c>application/zpl</c> or <c>image/png</c>.</param>
/// <param name="Dpi">The resolution of a ZPL label (203 or 300), else null.</param>
public sealed record CarrierDocument(byte[] Content, string MediaType, int? Dpi = null)
{
    public const string Pdf = "application/pdf";
    public const string Zpl = "application/zpl";
    public const string Png = "image/png";
}

/// <summary>Names one parcel at the carrier.</summary>
public sealed record CarrierParcelReference(string CarrierParcelId, string TrackingNumber, string? Account, string? DestinationPostalCode = null, string? DestinationCountry = null);

/// <summary>
/// One tracking event. Webhooks are at-least-once and unordered (warehouse §4.12): <see cref="EventId"/>
/// deduplicates, <see cref="OccurredAt"/> orders.
/// </summary>
/// <param name="EventId">The carrier's event id, or a stable digest of (parcel, status, time) when it sends none.</param>
/// <param name="TrackingNumber">The parcel's tracking number.</param>
/// <param name="CarrierParcelId">The carrier's parcel id, when the event carries it.</param>
/// <param name="Status">Our normalized status.</param>
/// <param name="CarrierStatus">The carrier's own code.</param>
/// <param name="Description">The carrier's text.</param>
/// <param name="OccurredAt">When it happened.</param>
public sealed record CarrierTrackingEvent(
    string EventId,
    string TrackingNumber,
    string? CarrierParcelId,
    TrackingStatus Status,
    string CarrierStatus,
    string? Description,
    DateTimeOffset OccurredAt);

/// <summary>A carrier refused a request; <see cref="Exception.Message"/> carries its own message (never a credential).</summary>
public sealed class CarrierException(string carrier, string message, int? statusCode = null) : Exception($"{carrier}: {message}")
{
    public string Carrier { get; } = carrier;

    public int? StatusCode { get; } = statusCode;
}

/// <summary>A search for service points near an address (GB5).</summary>
public sealed record ServicePointQuery
{
    /// <summary>ISO 3166-1 alpha-2.</summary>
    public required string Country { get; init; }

    public required string PostalCode { get; init; }

    public string? City { get; init; }

    public string? Street { get; init; }

    public string? HouseNumber { get; init; }

    /// <summary>Only parcel lockers (24/7 machines), only staffed points, or both.</summary>
    public ServicePointKind? Kind { get; init; }

    /// <summary>At most this many points; 1–50.</summary>
    public int Limit { get; init; } = 10;

    /// <summary>The carrier account; null for the default.</summary>
    public string? Account { get; init; }
}

/// <summary>A staffed pick-up point or a parcel locker.</summary>
public enum ServicePointKind
{
    PickupPoint,
    ParcelLocker,
}

/// <summary>One service point or parcel locker (GB5).</summary>
public sealed record ServicePoint
{
    /// <summary>The carrier's id, sent with the booking.</summary>
    public required string Id { get; init; }

    public required string Carrier { get; init; }

    public required string Name { get; init; }

    public ServicePointKind Kind { get; init; }

    public required ShippingAddress Address { get; init; }

    public double? Latitude { get; init; }

    public double? Longitude { get; init; }

    /// <summary>Distance from the searched address in metres, when the carrier states it.</summary>
    public int? DistanceMetres { get; init; }

    /// <summary>Opening hours as the carrier states them, per day (<c>monday</c> → <c>["08:00-12:00", "13:00-18:00"]</c>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> OpeningHours { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}
