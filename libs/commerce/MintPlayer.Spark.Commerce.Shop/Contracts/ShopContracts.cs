using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Shop.Contracts;

// The TypeScript contracts of @mintplayer/ng-spark/commerce/shop (X20 item 2, docs/guide-typescript-contracts.md).
// Ids travel in bodies and query strings, never in paths (RavenDB ids contain slashes). Enum values travel as their
// names. Checkout item ids are "shop:{variantId}".

// ---- product page ----------------------------------------------------------------------------------------------------

/// <summary>A published product as its page shows it (<c>GET /spark/commerce/shop/products?slug=</c>).</summary>
/// <param name="Kind"><c>Goods</c>, <c>Digital</c> or <c>Service</c>.</param>
/// <param name="UnitOfMeasure">GA3: <c>None</c>, <c>Kilogram</c>, <c>Litre</c>, <c>Metre</c>, <c>SquareMetre</c>, <c>CubicMetre</c> or <c>Piece</c>.</param>
/// <param name="UnitSymbol">GA3: what a unit price is per (<c>kg</c>, <c>l</c>, …); empty without a unit of measure.</param>
/// <param name="LegalGuaranteeMonths">GA8: the legal guarantee the notice states (Directive (EU) 2019/771 Art. 10).</param>
/// <param name="GuaranteeNotice">GA8: the host's notice text (<c>GuaranteeNotice</c>); null shows the translation <c>commerceShop.guarantee.notice</c>.</param>
/// <param name="WithdrawalExemption">The CRD Art. 16 point that exempts the product from the withdrawal right, or null.</param>
[SparkContract]
public sealed record SparkShopProduct(
    string Id,
    string Slug,
    TranslatedString Name,
    TranslatedString? Description,
    string? Brand,
    string? Mpn,
    string Kind,
    string? OrganizationId,
    string? SellerName,
    string Channel,
    string? Currency,
    int? MinimumAge,
    IReadOnlyList<string> CategoryIds,
    IReadOnlyList<SparkShopMedia> Media,
    IReadOnlyList<SparkShopOption> Attributes,
    IReadOnlyList<SparkShopVariant> Variants,
    string UnitOfMeasure,
    string UnitSymbol,
    decimal? UnitPriceReference,
    SparkShopSafety? Safety,
    int LegalGuaranteeMonths,
    TranslatedString? GuaranteeNotice,
    SparkShopDurability? Durability,
    string? WithdrawalExemption,
    bool ReviewsEnabled,
    int ReviewCount,
    decimal? RatingAverage);

/// <summary>A variant of the product page.</summary>
/// <param name="ItemId">Checkout's item id (<c>shop:{variantId}</c>).</param>
/// <param name="FinalPrice">FinalPrice of one unit (§4.4): VAT and every mandatory fee included. A sale is shown by Promotions' <c>&lt;spark-sale-price&gt;</c>, which carries the prior price (GA3).</param>
/// <param name="UnitPrice">GA3: the price per <c>UnitPriceReference</c> unit of measure, computed from <paramref name="FinalPrice"/>; null without a unit of measure.</param>
/// <param name="Availability"><c>InStock</c>, <c>LimitedAvailability</c> or <c>OutOfStock</c>.</param>
/// <param name="Available">Units left, when the variant counts its stock and few are left; null otherwise.</param>
[SparkContract]
public sealed record SparkShopVariant(
    string Id,
    string ItemId,
    string Sku,
    string? Gtin,
    IReadOnlyList<SparkShopOption> Options,
    Money FinalPrice,
    Money? UnitPrice,
    decimal? NetQuantity,
    string Availability,
    int? Available);

/// <summary>A gallery image (GB13): <paramref name="VariantIds"/> empty shows it for every variant.</summary>
[SparkContract]
public sealed record SparkShopMedia(string Url, TranslatedString Alt, IReadOnlyList<string> VariantIds);

/// <summary>A variant option or a declared attribute.</summary>
[SparkContract]
public sealed record SparkShopOption(string Name, string Value);

/// <summary>GA7: the product's safety information (Reg. (EU) 2023/988 Art. 19), shown in a fixed block.</summary>
[SparkContract]
public sealed record SparkShopSafety(
    string? ManufacturerName,
    string? ManufacturerAddress,
    string? ManufacturerContact,
    string? ResponsiblePersonName,
    string? ResponsiblePersonAddress,
    string? ResponsiblePersonContact,
    string? Identifier,
    IReadOnlyList<string> Gtins,
    string? PictureUrl,
    TranslatedString? Warnings);

/// <summary>GA8: a producer's commercial guarantee of durability.</summary>
[SparkContract]
public sealed record SparkShopDurability(int Months, string? Guarantor, TranslatedString? Terms);

// ---- catalogue ---------------------------------------------------------------------------------------------------------

/// <summary>
/// A catalogue page (<c>GET /spark/commerce/shop/catalog?q=&amp;category=&amp;facet=colour:red&amp;price=0&amp;sort=&amp;skip=&amp;take=</c>).
/// </summary>
/// <param name="Typo">Whether typo tolerance was applied (the index runs Lucene, D38).</param>
[SparkContract]
public sealed record SparkShopCatalogPage(
    IReadOnlyList<SparkShopCatalogItem> Items,
    int Total,
    IReadOnlyList<SparkShopFacet> Facets,
    IReadOnlyList<SparkShopPriceRange> PriceRanges,
    bool Typo);

/// <summary>A product in a catalogue list.</summary>
/// <param name="PriceFrom">The lowest variant's FinalPrice of one unit (§4.4).</param>
[SparkContract]
public sealed record SparkShopCatalogItem(
    string Id,
    string Slug,
    TranslatedString Name,
    string? Brand,
    string? ImageUrl,
    TranslatedString? ImageAlt,
    Money? PriceFrom,
    bool VariesInPrice,
    int ReviewCount,
    decimal? RatingAverage,
    string? SellerName);

/// <summary>A facet: its key and the values with their counts (from the catalogue index, GB13).</summary>
[SparkContract]
public sealed record SparkShopFacet(string Key, IReadOnlyList<SparkShopFacetValue> Values);

/// <param name="Term">The filter to send back (<c>facet={Term}</c>).</param>
[SparkContract]
public sealed record SparkShopFacetValue(string Term, string Value, int Count, bool Selected);

/// <summary>A price range of the price facet; <paramref name="Index"/> is what <c>price=</c> sends back.</summary>
[SparkContract]
public sealed record SparkShopPriceRange(int Index, Money? From, Money? To, int Count, bool Selected);

/// <summary>A category (<c>GET /spark/commerce/shop/categories</c>).</summary>
[SparkContract]
public sealed record SparkShopCategory(string Id, string Slug, TranslatedString Name, TranslatedString? Description, string? ParentId, IReadOnlyList<string> FacetKeys, int SortOrder);

// ---- delivery step -----------------------------------------------------------------------------------------------------

/// <summary>An address of the delivery step.</summary>
[SparkContract]
public sealed record SparkShopAddress(
    string Name,
    string? Company,
    string Street,
    string? HouseNumber,
    string? Addition,
    string PostalCode,
    string City,
    string Country,
    string? Email,
    string? Phone);

/// <summary>A delivery method the basket can be sent with (<c>GET /spark/commerce/shop/delivery/options?country=&amp;postalCode=</c>).</summary>
/// <param name="Kind"><c>Carrier</c> or <c>Pickup</c>.</param>
/// <param name="ServicePoint">GB5: the buyer picks a service point or locker (<c>&lt;spark-service-point-picker&gt;</c>).</param>
/// <param name="Customs">GB6: <c>None</c> inside the EU customs and VAT territory, else why customs apply.</param>
/// <param name="Incoterm">GB6: <c>Dap</c> (the buyer pays duties) or <c>Ddp</c>, shown when customs apply.</param>
[SparkContract]
public sealed record SparkShopDeliveryRate(
    string Method,
    string Kind,
    string? Carrier,
    Money Price,
    bool Free,
    Money? FreeFrom,
    bool ServicePoint,
    string Customs,
    string? Incoterm,
    DateOnly? PromisedFrom,
    DateOnly? PromisedBy,
    string? PickupLocation);

/// <summary>The rates for the current basket and the buyer's current choice.</summary>
/// <param name="NeedsDelivery">Whether the basket holds goods (the step is shown only then).</param>
[SparkContract]
public sealed record SparkShopDeliveryOptions(bool NeedsDelivery, IReadOnlyList<SparkShopDeliveryRate> Rates, SparkShopDeliveryChoice? Chosen);

/// <summary><c>POST /spark/commerce/shop/delivery</c>: the buyer's choice. A service point is named by id and carrier; the server looks it up again.</summary>
[SparkContract]
public sealed record SparkShopDeliveryRequest(SparkShopAddress ShipTo, string Method, string? ServicePointId = null, string? ServicePointCarrier = null);

/// <summary>The buyer's delivery choice.</summary>
[SparkContract]
public sealed record SparkShopDeliveryChoice(SparkShopAddress ShipTo, string Method, string? ServicePointId, string? ServicePointName, IReadOnlyList<string> ServicePointAddressLines);

// ---- the buyer's order -------------------------------------------------------------------------------------------------

/// <summary>The shop's side of the caller's own order (<c>GET /spark/commerce/shop/orders?number=</c>).</summary>
/// <param name="State"><c>AwaitingPayment</c>, <c>Paid</c>, <c>PartiallyShipped</c>, <c>Shipped</c>, <c>Delivered</c> or <c>Cancelled</c>.</param>
[SparkContract]
public sealed record SparkShopOrder(
    string OrderNumber,
    string? OrganizationId,
    string State,
    string? Method,
    string? ServicePointName,
    int? MinimumAge,
    IReadOnlyList<SparkShopOrderLine> Lines,
    IReadOnlyList<SparkShopShipment> Shipments,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? DeliveredAt);

[SparkContract]
public sealed record SparkShopOrderLine(string LineId, string ItemId, string ProductId, TranslatedString? Title, int Quantity, int ShippedQuantity, DateTimeOffset? DeliveredAt, bool Reviewable);

/// <param name="Status">Shipping's tracking status (<c>Labelled</c>, <c>InTransit</c>, …, <c>Delivered</c>).</param>
[SparkContract]
public sealed record SparkShopShipment(bool Return, string? Carrier, string Status, IReadOnlyList<SparkShopParcel> Parcels, DateOnly? PromisedBy);

[SparkContract]
public sealed record SparkShopParcel(int Number, string? TrackingNumber, string? TrackingUrl, string Status);

/// <summary>The shop's refusal (HTTP 409); checkout's own codes come back in the same shape.</summary>
[SparkContract]
public sealed record SparkShopRefusal(string Code, string? Subject);

// ---- staff -------------------------------------------------------------------------------------------------------------

/// <summary><c>POST /spark/commerce/shop/staff/ship</c>: books a shipment of some units (ShopOnly, GB5 multi-parcel).</summary>
/// <param name="OrderId">Checkout's order id.</param>
/// <param name="OrganizationId">The seller whose part ships (null: the platform's own).</param>
/// <param name="Lines">Units per line; empty ships everything not shipped yet.</param>
/// <param name="Parcels">One label per parcel; empty ships one parcel of the lines' weight.</param>
[SparkContract]
public sealed record SparkShopShipRequest(string OrderId, string? OrganizationId, IReadOnlyList<SparkShopShipLine> Lines, IReadOnlyList<SparkShopParcelRequest> Parcels);

[SparkContract]
public sealed record SparkShopShipLine(string LineId, int Quantity);

[SparkContract]
public sealed record SparkShopParcelRequest(int WeightGrams, int LengthMm = 0, int WidthMm = 0, int HeightMm = 0);

[SparkContract]
public sealed record SparkShopShipResult(string ShipmentId, IReadOnlyList<string> TrackingNumbers);

/// <summary>
/// <c>POST /spark/commerce/shop/staff/inspect</c>: the disposition of returned goods (GA8). <c>Restock</c> puts the units
/// back on sale exactly once (I4); diminished value (CRD Art. 14(2)) may be deducted with a reason while the refund is
/// still withheld.
/// </summary>
/// <param name="Disposition"><c>Restock</c>, <c>Inspect</c>, <c>Quarantine</c> or <c>Dispose</c>.</param>
[SparkContract]
public sealed record SparkShopInspectRequest(string OrderId, string? OrganizationId, string ReturnId, string Disposition, IReadOnlyList<SparkShopDeduction>? Deductions = null, string? Note = null);

[SparkContract]
public sealed record SparkShopDeduction(string LineId, Money Amount, string Reason);

/// <summary>What an inspection recorded.</summary>
[SparkContract]
public sealed record SparkShopInspectResult(string ReturnId, string Disposition);

/// <summary><c>POST /spark/commerce/shop/staff/return-label</c>: a return label for a return Checkout expects (GB5).</summary>
[SparkContract]
public sealed record SparkShopReturnLabelRequest(string OrderId, string? OrganizationId, string ReturnId);

// ---- reviews (GC5) -----------------------------------------------------------------------------------------------------

/// <summary>A product's published reviews (<c>GET /spark/commerce/shop/reviews?productId=&amp;skip=</c>).</summary>
/// <param name="Distribution">How many reviews gave 1, 2, 3, 4 and 5 stars.</param>
/// <param name="PreModerated">Whether reviews are checked by a moderator before they are shown (the verification statement says so).</param>
[SparkContract]
public sealed record SparkShopReviewPage(string ProductId, int Count, decimal? Average, IReadOnlyList<int> Distribution, IReadOnlyList<SparkShopReview> Reviews, bool PreModerated);

/// <summary>A published review. <c>Verified</c> is always true: only buyers of a delivered order line can review.</summary>
[SparkContract]
public sealed record SparkShopReview(
    string Id,
    int Rating,
    string? Title,
    string Body,
    string? DisplayName,
    string? Language,
    bool Incentivised,
    bool Verified,
    DateTimeOffset? PostedAt,
    string? VariantId);

/// <summary><c>POST /spark/commerce/shop/reviews</c>: a review of a delivered line of the caller's own order.</summary>
[SparkContract]
public sealed record SparkShopReviewRequest(string OrderNumber, string LineId, int Rating, string Body, string? Title = null, string? DisplayName = null, string? Language = null, bool Incentivised = false);

/// <summary>What the review was stored as: <c>Pending</c> while pre-moderated, else <c>Published</c>.</summary>
[SparkContract]
public sealed record SparkShopReviewResult(string Id, string Status);

/// <summary>A review waiting for a moderator (<c>GET /spark/commerce/shop/reviews/pending</c>, <c>Review/Moderation</c>).</summary>
[SparkContract]
public sealed record SparkShopPendingReview(string Id, string ProductId, TranslatedString? ProductName, int Rating, string? Title, string Body, bool Incentivised, DateTimeOffset? PostedAt);

/// <summary>
/// <c>POST /spark/commerce/shop/reviews/decide</c>: publish, or reject for one of the allowed reasons (<c>Spam</c>,
/// <c>Offensive</c>, <c>Unrelated</c>, <c>PersonalData</c>, <c>ConflictOfInterest</c>, <c>Illegal</c>): never because it is negative.
/// </summary>
[SparkContract]
public sealed record SparkShopReviewDecision(string ReviewId, bool Publish, string? Reason = null);

// ---- customer account area (GB10) ---------------------------------------------------------------------------------

/// <summary>A saved address of the signed-in buyer.</summary>
[SparkContract]
public sealed record SparkShopAddressBookEntry(string EntryId, string? Label, SparkShopAddress Address, bool IsDefault);

/// <summary>The signed-in buyer's address book (<c>GET /spark/commerce/shop/account/addresses</c>), in saved order; one entry is the default.</summary>
[SparkContract]
public sealed record SparkShopAddressBook(IReadOnlyList<SparkShopAddressBookEntry> Entries);

/// <summary><c>POST /spark/commerce/shop/account/addresses</c>: adds an entry, or replaces <paramref name="EntryId"/>'s.</summary>
[SparkContract]
public sealed record SparkShopAddressSaveRequest(SparkShopAddress Address, string? EntryId = null, string? Label = null, bool IsDefault = false);

/// <summary><c>POST /spark/commerce/shop/account/addresses/delete</c>.</summary>
[SparkContract]
public sealed record SparkShopAddressDeleteRequest(string EntryId);

/// <summary>Where a checkout item lives on the storefront (<c>GET /spark/commerce/shop/item?id=</c>): the product page and the variant.</summary>
[SparkContract]
public sealed record SparkShopItemLink(string Slug, string VariantId);
