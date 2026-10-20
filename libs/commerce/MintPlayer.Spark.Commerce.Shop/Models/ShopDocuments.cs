using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Moderation;
using MintPlayer.Spark.Organizations;

namespace MintPlayer.Spark.Commerce.Shop.Models;

// The catalogue of PRD §4.10: products with variants beside them (each its own document, persistent-object type and
// quota), and platform categories. Products are owned by the seller's organization (X8): its members edit them and the
// product alone is published. Variants copy the product's OrganizationId on every save (ShopVariantInterceptor), so the
// organization row rule needs no join.

/// <summary>A product (PRD §4.10): what is sold, by whom, in which channel, with its legal information.</summary>
/// <remarks>
/// <para><b>Publishable (X8).</b> While <see cref="IsPublished"/> is true, everyone may list and read it and its variants
/// sell. Publishing needs the <c>Publish</c> role (Admin) and is refused while the product cannot sell: no active
/// variant, an invalid SKU or GTIN, an unknown channel, a seller that may not sell in it (D21, D43), a required legal
/// document missing (GA2), or — for goods — no manufacturer data (GA7, Reg. (EU) 2023/988 Art. 19).</para>
/// <para>The fields <see cref="CategoryPath"/>, <see cref="FacetValues"/>, <see cref="SearchText"/>,
/// <see cref="PriceFromMinor"/>, <see cref="ReviewCount"/> and <see cref="RatingAverage"/> are maintained by the shop
/// (on every product or variant save, and on review decisions): the catalogue index map reads them (X19).</para>
/// </remarks>
[RequiresOrganizationRole("Publish", OrganizationRole.Admin)]
public sealed class ShopProduct : IOrganizationPublishable
{
    public string? Id { get; set; }

    /// <summary>The seller organization; null when the platform sells in its own name.</summary>
    public string? OrganizationId { get; set; }

    /// <summary>Whether the product is on sale and its page public (X8).</summary>
    public bool IsPublished { get; set; }

    public TranslatedString Name { get; set; } = new();

    /// <summary>The public page's URL segment (<c>/shop/products/{slug}</c>): lower-case letters, digits and dashes, unique. Set from the name when empty.</summary>
    public string Slug { get; set; } = "";

    public TranslatedString? Description { get; set; }

    /// <summary>The brand (schema.org <c>brand</c>, the feeds' <c>brand</c>, a facet).</summary>
    public string? Brand { get; set; }

    /// <summary>The manufacturer part number (schema.org <c>mpn</c>, the feeds' <c>mpn</c>).</summary>
    public string? Mpn { get; set; }

    /// <summary>The channel (<c>Spark:Commerce:Channels:{name}</c>, D2) every variant sells in. Permanent once published.</summary>
    public string Channel { get; set; } = "";

    public EShopProductKind Kind { get; set; } = EShopProductKind.Goods;

    /// <summary>The categories it is listed in (navigation and the category facet).</summary>
    public List<string> CategoryIds { get; set; } = [];

    /// <summary>The unit price of every variant without its own, on a gross (VAT-inclusive) basis.</summary>
    public Money? Price { get; set; }

    /// <summary>The VAT rate in basis points (2100 = 21 %); null leaves the rate to invoicing.</summary>
    public int? VatRateBasisPoints { get; set; }

    // ---- GA3: unit price (Price Indication Directive 98/6/EC Art. 3) ------------------------------------------------

    /// <summary>The unit of measure of a product sold by quantity; <see cref="EUnitOfMeasure.None"/> shows no unit price.</summary>
    public EUnitOfMeasure UnitOfMeasure { get; set; } = EUnitOfMeasure.None;

    /// <summary>The net quantity in <see cref="UnitOfMeasure"/> (0.75 for a 75 cl bottle in litres); a variant may override it.</summary>
    public decimal? NetQuantity { get; set; }

    /// <summary>The quantity the unit price refers to (1 = per kg; 0.1 = per 100 g where a member state allows it). Default 1.</summary>
    public decimal? UnitPriceReference { get; set; }

    // ---- GA7: GPSR product information (Reg. (EU) 2023/988 Art. 19), the ProductSafety attribute group ---------------

    /// <summary>The manufacturer's name, registered trade name or trade mark (Art. 19(a)).</summary>
    public string? ManufacturerName { get; set; }

    /// <summary>The manufacturer's postal address (Art. 19(a)).</summary>
    public string? ManufacturerAddress { get; set; }

    /// <summary>The manufacturer's electronic address: an e-mail address or a website (Art. 19(a)).</summary>
    public string? ManufacturerContact { get; set; }

    /// <summary>The manufacturer's country (ISO 3166-1 alpha-2): outside the EU, an EU responsible person is required (Art. 19(b), Art. 16 and Reg. 2019/1020 Art. 4).</summary>
    public string? ManufacturerCountry { get; set; }

    /// <summary>The EU responsible person's name (Art. 19(b)); required when the manufacturer is established outside the EU.</summary>
    public string? ResponsiblePersonName { get; set; }

    /// <summary>The EU responsible person's postal address (Art. 19(b)).</summary>
    public string? ResponsiblePersonAddress { get; set; }

    /// <summary>The EU responsible person's electronic address (Art. 19(b)).</summary>
    public string? ResponsiblePersonContact { get; set; }

    /// <summary>The product identifier: type, batch or serial number, or the model (Art. 19(c)); variants' GTINs are listed beside it.</summary>
    public string? SafetyIdentifier { get; set; }

    /// <summary>A picture of the product that identifies it (Art. 19(c)).</summary>
    public string? SafetyPictureUrl { get; set; }

    /// <summary>Warnings and safety information, per language (Art. 19(d): in a language easily understood by consumers of the member state).</summary>
    public TranslatedString? SafetyWarnings { get; set; }

    // ---- GA8: commercial durability guarantee (Directive (EU) 2024/825, CRD Art. 6(1)(la) as inserted) -------------

    /// <summary>A producer's commercial guarantee of durability, in months, when it gives one for the whole product beyond two years.</summary>
    public int? DurabilityGuaranteeMonths { get; set; }

    /// <summary>Who gives the durability guarantee (the producer).</summary>
    public string? DurabilityGuarantor { get; set; }

    /// <summary>Its terms, per language.</summary>
    public TranslatedString? DurabilityGuaranteeTerms { get; set; }

    // ---- GB6: customs, GC9: dangerous goods ----------------------------------------------------------------------------

    /// <summary>The HS code (6, 8 or 10 digits) for customs outside the EU customs territory; a variant may override it.</summary>
    public string? HsCode { get; set; }

    /// <summary>The country of origin (ISO 3166-1 alpha-2); a variant may override it.</summary>
    public string? OriginCountry { get; set; }

    /// <summary>GC9: a dangerous-goods class restricts the carrier services offered (Shipping).</summary>
    public DangerousGoodsClass? DangerousGoods { get; set; }

    /// <summary>GC9: the UN number, when the class needs one.</summary>
    public string? UnNumber { get; set; }

    // ---- GB12 ----------------------------------------------------------------------------------------------------------

    /// <summary>GB12: buyers attest to be at least this old at checkout; the pickup slip and the handover carry an ID-check marker.</summary>
    public int? MinimumAge { get; set; }

    // ---- GB13: media and declared attributes --------------------------------------------------------------------------

    /// <summary>The gallery: images with alt text (required, per language), optionally shown for some variants only.</summary>
    public List<ShopProductMedia> Media { get; set; } = [];

    /// <summary>Declared attributes (material, fit, …): shown on the page and filterable as facets.</summary>
    public List<ShopProductAttribute> Attributes { get; set; } = [];

    // ---- D20 / GA1 ---------------------------------------------------------------------------------------------------

    /// <summary>The seller refund policy for buyer cancellations (D20), clamped to the channel's bounds. Never applies to the withdrawal (GA1).</summary>
    public RefundPolicyKind? RefundPolicy { get; set; }

    public int? RefundDaysBefore { get; set; }

    public int? RefundFeeBasisPoints { get; set; }

    /// <summary>
    /// The CRD Art. 16 point that exempts the product from the withdrawal right (<c>16(c)</c> made to the consumer's
    /// specifications, <c>16(d)</c> liable to deteriorate rapidly, <c>16(e)</c> sealed for health or hygiene, …); null
    /// when goods carry the right.
    /// </summary>
    public string? WithdrawalExemption { get; set; }

    /// <summary>GC5: whether buyers may review the product.</summary>
    public bool ReviewsEnabled { get; set; } = true;

    // ---- maintained by the shop ---------------------------------------------------------------------------------------

    /// <summary>The categories and their ancestors (the category facet matches a parent too).</summary>
    public List<string> CategoryPath { get; set; } = [];

    /// <summary>The facet terms <c>{key}:{value}</c> of the brand, the declared attributes and the active variants' options.</summary>
    public List<string> FacetValues { get; set; } = [];

    /// <summary>The searchable texts: every language of the name, the brand, the SKUs and GTINs.</summary>
    public List<string> SearchText { get; set; } = [];

    /// <summary>The lowest active variant price, in minor units of <see cref="Currency"/> (the price facet and sort).</summary>
    public long? PriceFromMinor { get; set; }

    public string? Currency { get; set; }

    /// <summary>Whether the active variants have different prices ("from" in a listing).</summary>
    public bool PriceVaries { get; set; }

    public int ReviewCount { get; set; }

    public decimal? RatingAverage { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A gallery item (GB13).</summary>
public sealed class ShopProductMedia
{
    /// <summary>An absolute URL or a path on the public origin.</summary>
    public string Url { get; set; } = "";

    /// <summary>The alternative text, required (WCAG 2.1 SC 1.1.1), per language.</summary>
    public TranslatedString Alt { get; set; } = new();

    /// <summary>The variants the image shows; empty for every variant.</summary>
    public List<string> VariantIds { get; set; } = [];

    public int SortOrder { get; set; }
}

/// <summary>A declared attribute of a product (GB13): a facet <c>{Name}:{Value}</c>.</summary>
public sealed class ShopProductAttribute
{
    /// <summary>The facet key (lower-case letters, digits and dashes): <c>material</c>.</summary>
    public string Name { get; set; } = "";

    public string Value { get; set; } = "";
}

/// <summary>A variant of a product (size, colour): its own SKU, GTIN, price, stock quota and customs data.</summary>
public sealed class ShopVariant : IOrganizationOwned
{
    public string? Id { get; set; }

    /// <summary>Copied from the product on every save.</summary>
    public string? OrganizationId { get; set; }

    public string ProductId { get; set; } = "";

    /// <summary>The stock-keeping unit: letters, digits, <c>-</c>, <c>_</c>, <c>.</c> and <c>/</c>; unique per seller.</summary>
    public string Sku { get; set; } = "";

    /// <summary>A GTIN-8, -12, -13 or -14 with a valid GS1 check digit, or null.</summary>
    public string? Gtin { get; set; }

    /// <summary>The options that tell the variant apart: <c>size = M</c>, <c>colour = Red</c> (facets too).</summary>
    public List<ShopVariantOption> Options { get; set; } = [];

    /// <summary>The variant's own unit price; null takes the product's.</summary>
    public Money? Price { get; set; }

    /// <summary>GA3: the variant's own net quantity; null takes the product's.</summary>
    public decimal? NetQuantity { get; set; }

    /// <summary>The gross weight of one unit in grams (shipping rates, labels, customs).</summary>
    public int? WeightGrams { get; set; }

    /// <summary>GB6: overrides the product's HS code.</summary>
    public string? HsCode { get; set; }

    /// <summary>GB6: overrides the product's country of origin.</summary>
    public string? OriginCountry { get; set; }

    /// <summary>Whether the variant's units are counted (its own quota). Off sells without limit (made to order, digital).</summary>
    public bool TrackInventory { get; set; } = true;

    /// <summary>
    /// The total units the variant sells (ShopOnly): sold and held units count against it, so raise it when goods arrive.
    /// Ignored in Warehouse mode, where the warehouse's stock feed sets the quota (<see cref="ISparkShopStock"/>).
    /// </summary>
    public int Capacity { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    /// <summary>When the variant's quota was created; null while the product was never published.</summary>
    public DateTimeOffset? ProvisionedAt { get; set; }
}

/// <summary>An option value of a variant.</summary>
public sealed class ShopVariantOption
{
    /// <summary>The option key (lower-case letters, digits and dashes): <c>size</c>, <c>colour</c>.</summary>
    public string Name { get; set; } = "";

    public string Value { get; set; } = "";
}

/// <summary>A platform category (navigation and the category facet). Edited by platform admins (<c>ManageSellers/Commerce</c>).</summary>
public sealed class ShopCategory
{
    public string? Id { get; set; }

    public TranslatedString Name { get; set; } = new();

    /// <summary>The page's URL segment (<c>/shop/categories/{slug}</c>), unique.</summary>
    public string Slug { get; set; } = "";

    public TranslatedString? Description { get; set; }

    /// <summary>The parent category; null for a top-level one.</summary>
    public string? ParentId { get; set; }

    /// <summary>The facet keys the category page offers (<c>size</c>, <c>colour</c>, <c>brand</c>); empty offers every key found.</summary>
    public List<string> FacetKeys { get; set; } = [];

    public int SortOrder { get; set; }
}

// ---- orders ---------------------------------------------------------------------------------------------------------

/// <summary>
/// The shop's side of an order (PRD §4.10 "Paid → Shipped → Delivered"), one per order and seller: the delivery the
/// buyer chose, what was shipped in which shipment, what came back. Owned by the seller (X8): its members see and ship it.
/// Written by the shop only (order events, shipment events, staff endpoints).
/// </summary>
public sealed class ShopOrder : IOrganizationOwned
{
    public string? Id { get; set; }

    /// <summary>The seller; null for the platform's own sale.</summary>
    public string? OrganizationId { get; set; }

    /// <summary>Checkout's order.</summary>
    public string OrderId { get; set; } = "";

    public string OrderNumber { get; set; } = "";

    public EShopOrderState State { get; set; } = EShopOrderState.AwaitingPayment;

    /// <summary>Who ships (fixed when the order is placed).</summary>
    public ShopFulfilmentMode Fulfilment { get; set; }

    public string Currency { get; set; } = "EUR";

    /// <summary>The delivery address (the buyer's), or the service point's holder name and the buyer's contact.</summary>
    public ShippingAddress? ShipTo { get; set; }

    /// <summary>The method chosen (<c>Spark:Commerce:Shipping:Methods:{key}</c>).</summary>
    public string? Method { get; set; }

    public string? Carrier { get; set; }

    public string? ServiceCode { get; set; }

    /// <summary>GB5: the service point or locker chosen.</summary>
    public ShipmentServicePoint? ServicePoint { get; set; }

    /// <summary>What the buyer paid for delivery.</summary>
    public Money? DeliveryCost { get; set; }

    /// <summary>The least expensive standard delivery offered at the time (CRD Art. 13(2)): what a withdrawal refunds of the delivery.</summary>
    public Money? StandardDeliveryCost { get; set; }

    /// <summary>The order extra code that carries the delivery (<c>delivery:{method}</c>).</summary>
    public string? DeliveryExtraCode { get; set; }

    public List<ShopOrderLine> Lines { get; set; } = [];

    public List<ShopOrderShipment> Shipments { get; set; } = [];

    public List<ShopOrderReturn> Returns { get; set; } = [];

    /// <summary>GB12: the strictest minimum age of the lines; the handover checks an ID.</summary>
    public int? MinimumAge { get; set; }

    public DateTimeOffset? PaidAt { get; set; }

    public DateTimeOffset? ShippedAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An order line of goods.</summary>
public sealed class ShopOrderLine
{
    public string LineId { get; set; } = "";

    public string ItemId { get; set; } = "";

    public string VariantId { get; set; } = "";

    public string ProductId { get; set; } = "";

    public string Sku { get; set; } = "";

    public TranslatedString? Title { get; set; }

    public int Quantity { get; set; }

    /// <summary>The unit price paid (gross): customs value (GB6).</summary>
    public Money? UnitPrice { get; set; }

    /// <summary>A line Checkout refused at confirmation (D40) or cancelled: never shipped.</summary>
    public bool Cancelled { get; set; }

    public int ShippedQuantity { get; set; }

    public DateTimeOffset? ShippedAt { get; set; }

    /// <summary>When the buyer received the last units: the withdrawal and guarantee periods start here (Checkout).</summary>
    public DateTimeOffset? DeliveredAt { get; set; }
}

/// <summary>A shipment of the order: outbound, or a return label.</summary>
public sealed class ShopOrderShipment
{
    public string ShipmentId { get; set; } = "";

    public bool Return { get; set; }

    /// <summary>For a return label: Checkout's return id.</summary>
    public string? ReturnId { get; set; }

    public List<ShopShippedLine> Lines { get; set; } = [];

    public DateTimeOffset? LabelledAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }
}

/// <summary>Units of an order line in a shipment or return.</summary>
public sealed class ShopShippedLine
{
    public string LineId { get; set; } = "";

    public int Quantity { get; set; }
}

/// <summary>Goods expected back (Checkout's <c>ReturnExpected</c>, GA1/GA8) and what became of them.</summary>
public sealed class ShopOrderReturn
{
    public string ReturnId { get; set; } = "";

    public ReturnReasonCode Reason { get; set; }

    public ReturnDisposition DefaultDisposition { get; set; }

    public bool SellerPaysReturnShipping { get; set; }

    public List<ShopShippedLine> Lines { get; set; } = [];

    public string? ReturnShipmentId { get; set; }

    public DateTimeOffset? LabelMailedAt { get; set; }

    /// <summary>The carrier's scan of the return parcel: the buyer's proof of dispatch (CRD Art. 13(3)).</summary>
    public DateTimeOffset? ProofOfDispatchAt { get; set; }

    /// <summary>The carrier delivered the return to the seller.</summary>
    public DateTimeOffset? ArrivedAt { get; set; }

    /// <summary>When the receipt was registered with Checkout.</summary>
    public DateTimeOffset? ReceivedAt { get; set; }

    /// <summary>The disposition decided at inspection (Restock puts the units back on sale, I4).</summary>
    public ReturnDisposition? Disposition { get; set; }

    public DateTimeOffset? InspectedAt { get; set; }

    public string? InspectedBy { get; set; }

    public string? InspectionNote { get; set; }
}

/// <summary>
/// A buyer's delivery choice for the checkout (the delivery step): the address, the method and the service point.
/// Its id travels in the basket's choices (<c>shop.delivery</c>); it is personal data and expires after
/// <c>Delivery:ChoiceLifetime</c>.
/// </summary>
public sealed class ShopDeliveryChoice
{
    public string? Id { get; set; }

    /// <summary>The guest's visitor id (X1) that made the choice.</summary>
    public string? VisitorId { get; set; }

    /// <summary>The signed-in buyer that made the choice.</summary>
    public string? UserId { get; set; }

    public ShippingAddress ShipTo { get; set; } = new();

    public string Method { get; set; } = "";

    public ShipmentServicePoint? ServicePoint { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

// ---- reviews (GC5) ----------------------------------------------------------------------------------------------------

/// <summary>
/// A verified-purchase review (GC5): written only by the buyer of a delivered order line, one per line. Moderated
/// through Moderation (flags, locks, the reviewers' decision while pre-moderated); never edited by the seller.
/// </summary>
public sealed class ShopProductReview : IModeratable
{
    public string? Id { get; set; }

    public string ProductId { get; set; } = "";

    public string? VariantId { get; set; }

    /// <summary>The seller of the product (copied, for reports).</summary>
    public string? OrganizationId { get; set; }

    /// <summary>The order and line that verify the purchase.</summary>
    public string OrderId { get; set; } = "";

    public string LineId { get; set; } = "";

    /// <summary>The buyer (IModeratable: stamped once, never changed).</summary>
    public string? AuthorId { get; set; }

    public DateTimeOffset? PostedAt { get; set; }

    /// <summary>The name shown with the review (the buyer's choice; may be a first name or a pseudonym).</summary>
    public string? DisplayName { get; set; }

    /// <summary>1 to 5.</summary>
    public int Rating { get; set; }

    public string? Title { get; set; }

    public string Body { get; set; } = "";

    /// <summary>The BCP 47 language the review is written in.</summary>
    public string? Language { get; set; }

    /// <summary>The buyer declares a benefit for the review (UCPD Annex I point 23c): shown with the review.</summary>
    public bool Incentivised { get; set; }

    /// <summary>When the line was delivered (the verification).</summary>
    public DateTimeOffset? PurchaseDeliveredAt { get; set; }

    public EShopReviewStatus Status { get; set; } = EShopReviewStatus.Pending;

    public DateTimeOffset? DecidedAt { get; set; }

    public string? DecidedBy { get; set; }

    /// <summary>Why a review was rejected: never because it is negative (UCPD Annex I point 23c: "misrepresenting consumer reviews").</summary>
    public EShopReviewRejection? RejectionReason { get; set; }
}

// ---- lookups ----------------------------------------------------------------------------------------------------------

[SparkLookup("ShopProductKind")]
public enum EShopProductKind
{
    /// <summary>Physical goods: shipped, withdrawal right (CRD Art. 9), GPSR information required (GA7).</summary>
    Goods,
    /// <summary>Digital content not supplied on a tangible medium (CRD Art. 16(m) applies once supply began with consent).</summary>
    Digital,
    /// <summary>A service.</summary>
    Service,
}

/// <summary>GA3: the units of measure a unit price is shown in (PID Art. 2(b): kg, l, m, m², m³, or a customary single unit such as per item).</summary>
[SparkLookup("ShopUnitOfMeasure")]
public enum EUnitOfMeasure
{
    None,
    Kilogram,
    Litre,
    Metre,
    SquareMetre,
    CubicMetre,
    Piece,
}

[SparkLookup("ShopOrderState")]
public enum EShopOrderState
{
    AwaitingPayment,
    Paid,
    PartiallyShipped,
    Shipped,
    Delivered,
    Cancelled,
}

[SparkLookup("ShopReviewStatus")]
public enum EShopReviewStatus
{
    Pending,
    Published,
    Rejected,
}

/// <summary>The only reasons a review may be refused (never its rating).</summary>
[SparkLookup("ShopReviewRejection")]
public enum EShopReviewRejection
{
    Spam,
    Offensive,
    Unrelated,
    PersonalData,
    ConflictOfInterest,
    Illegal,
}
