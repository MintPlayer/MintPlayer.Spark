using MintPlayer.Spark.Replication.Abstractions;

// A generic contract library (warehouse PRD §4.3): a host consumes only the contracts it binds in
// Spark:Replication:Sources:{TargetCollection}; the others are its own documents, or unused.
[assembly: SparkReplicaContracts]

namespace MintPlayer.Spark.Fulfilment.Contracts;

/// <summary>
/// The collections of the fulfilment contracts (warehouse PRD §4.3). Each is both the owner's collection (the
/// class's <c>SourceCollection</c>) and the consumer's replica collection, because both sides store the same class.
/// A host binds the ones it consumes, for example a warehouse:
/// <code>
/// "Spark": { "Replication": { "Sources": {
///     "FulfilmentOrders": [ "Shop" ], "FulfilmentCatalogItems": [ "Shop" ], "FulfilmentExpectedReturns": [ "Shop" ] } } }
/// </code>
/// </summary>
/// <remarks>
/// A changed shape is a new class in a new collection (<c>FulfilmentOrderV2</c> → <c>FulfilmentOrderV2s</c>), so a
/// new version runs beside the old one until every consumer moved (WD13: no version numbers).
/// </remarks>
public static class FulfilmentCollections
{
    // Shop → warehouse.
    public const string Orders = "FulfilmentOrders";
    public const string CatalogItems = "FulfilmentCatalogItems";
    public const string ExpectedReturns = "FulfilmentExpectedReturns";
    /// <summary>ASNs (Q-W9): published by the shop or an ERP.</summary>
    public const string ExpectedReceipts = "FulfilmentExpectedReceipts";

    // Warehouse → shop.
    public const string OrderStatuses = "FulfilmentOrderStatuses";
    public const string Shipments = "FulfilmentShipments";
    public const string StockLevels = "FulfilmentStockLevels";
    public const string ReturnReceipts = "FulfilmentReturnReceipts";
}

/// <summary>
/// A delivery address as the warehouse needs it for a label (PRD §4.3, "ship-to address"). Plain text: the shop
/// resolves its reference-data address into this snapshot, so the warehouse needs no reference data.
/// </summary>
public sealed class FulfilmentAddress
{
    /// <summary>The recipient's name.</summary>
    public string Name { get; set; } = "";
    public string? Company { get; set; }
    /// <summary>Street and house number, as the carrier prints them.</summary>
    public string Line1 { get; set; } = "";
    public string? Line2 { get; set; }
    public string PostalCode { get; set; } = "";
    public string City { get; set; } = "";
    /// <summary>State, province or region, where the country uses one.</summary>
    public string? Region { get; set; }
    /// <summary>ISO 3166-1 alpha-2, upper case: <c>BE</c>.</summary>
    public string CountryCode { get; set; } = "";
    /// <summary>For carrier notifications; only what the shop may pass on.</summary>
    public string? Phone { get; set; }
    public string? Email { get; set; }
    /// <summary>A carrier service point (pick-up point) id, when the buyer chose one at checkout.</summary>
    public string? ServicePointId { get; set; }
}

/// <summary>What happens to quantity the warehouse cannot ship (PRD §4.3, "Partial shipments and backorders").</summary>
public enum FulfilmentBackorderPolicy
{
    /// <summary>The quantity stays open and is re-allocated when stock arrives.</summary>
    Allow,
    /// <summary>The quantity is reported cancelled; the shop refunds it and lowers <c>sold</c> (§4.4 rule 1).</summary>
    CancelRemainder,
}

/// <summary>Why the buyer returns a line (commerce GA8). It sets the warehouse's default disposition (PRD §4.3, Returns).</summary>
public enum FulfilmentReturnReason
{
    /// <summary>The 14-day withdrawal (CRD Art. 9). Default: restock after inspection.</summary>
    Withdrawal,
    /// <summary>A legal-guarantee claim. Default: inspect, then quarantine.</summary>
    NonConformity,
    /// <summary>The wrong item was shipped. Default: restock.</summary>
    WrongItem,
    /// <summary>Damaged on the way. Default: inspect, then quarantine.</summary>
    DamagedInTransit,
    /// <summary>A return the seller allows beyond the law. Default: restock after inspection.</summary>
    SellerAllowed,
    Other,
}

/// <summary>What the warehouse did with a returned line (PRD §4.3, Returns step 4).</summary>
public enum FulfilmentReturnDisposition
{
    /// <summary>Back into sellable stock: raises <c>OnHandSellable</c> (§4.4).</summary>
    Restock,
    Quarantine,
    Scrap,
    ReturnToVendor,
}
