using MintPlayer.Spark.Replication.Abstractions;

namespace MintPlayer.Spark.Fulfilment.Contracts;

/// <summary>
/// <c>fulfilment-orders</c> (warehouse PRD §4.3): an order the shop wants shipped. Owner: the shop; consumer: the
/// warehouse, which creates its own <c>outbound/{Id}</c> working document and answers in
/// <see cref="FulfilmentOrderStatus"/>. The shop never reads the warehouse's documents and the warehouse never writes
/// this one (WD2).
/// </summary>
/// <remarks>
/// The id is the owner's and must be unique across every source the warehouse binds (the apply endpoint refuses an id
/// another source holds), so a shop prefixes it with its own name: <c>FulfilmentOrders/{shop}/{orderNumber}</c>.
/// </remarks>
[Replicated(SourceCollection = FulfilmentCollections.Orders)]
public partial class FulfilmentOrder
{
    public string? Id { get; set; }
    /// <summary>The owner of the goods, as the shop knows it (Q-W23: the warehouse maps it through an owner link).</summary>
    public string OwnerId { get; set; } = "";
    /// <summary>The sales channel, as the stock feed counts it (§4.4).</summary>
    public string Channel { get; set; } = "";
    /// <summary>The order number people quote; printed on the packing slip.</summary>
    public string OrderNumber { get; set; } = "";
    public DateTimeOffset PlacedAt { get; set; }
    public List<FulfilmentOrderLine> Lines { get; set; } = [];
    public FulfilmentAddress ShipTo { get; set; } = new();
    /// <summary>The carrier chosen at checkout, by its adapter code, or null for the warehouse's default.</summary>
    public string? CarrierCode { get; set; }
    public string? ServiceCode { get; set; }
    /// <summary>The first local day the warehouse may ship (a pre-order), or null.</summary>
    public DateOnly? ShipNotBefore { get; set; }
    /// <summary>Keep the order unallocated until every line can be allocated.</summary>
    public bool ShipComplete { get; set; }
    public FulfilmentBackorderPolicy BackorderPolicy { get; set; }
    /// <summary>Higher goes first.</summary>
    public int Priority { get; set; }
    /// <summary>Hold codes the shop sets (payment review, fraud check, buyer request); empty when releasable.</summary>
    public List<string> Holds { get; set; } = [];
    /// <summary>
    /// The shop asks the warehouse to cancel (WD2 handshake); the warehouse answers in
    /// <see cref="FulfilmentOrderStatus.Cancellation"/>. The shop lowers <c>sold</c> only on that answer (§4.4 rule 1).
    /// </summary>
    public bool CancellationRequested { get; set; }
    /// <summary>The lines to cancel, or empty for the whole order.</summary>
    public List<string> CancellationLineIds { get; set; } = [];
}

/// <summary>A line of a <see cref="FulfilmentOrder"/>.</summary>
public sealed class FulfilmentOrderLine
{
    /// <summary>Stable within the order: status, shipments and returns refer to it.</summary>
    public string LineId { get; set; } = "";
    public string Sku { get; set; } = "";
    /// <summary>Units of the SKU (the warehouse's base unit for it).</summary>
    public decimal Quantity { get; set; }
    /// <summary>The name printed on the packing slip.</summary>
    public string? Name { get; set; }
    /// <summary>A lot the line must come from (a recall replacement, a customer's lot), or null.</summary>
    public string? RequiredLotCode { get; set; }
}

/// <summary>
/// <c>catalog-items</c> (warehouse PRD §4.3, WD9): a sellable item as the shop owns it. The warehouse extends it with
/// its own item document (dimensions, packaging, policies) and never edits this one. Identity is (owner, SKU).
/// </summary>
/// <remarks>A discontinued item stays published while the warehouse still holds stock of it (§4.5, Items).</remarks>
[Replicated(SourceCollection = FulfilmentCollections.CatalogItems)]
public partial class FulfilmentCatalogItem
{
    public string? Id { get; set; }
    public string OwnerId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>GTIN-8/12/13/14 as the shop has them; the warehouse normalises to 14 digits.</summary>
    public List<string> Gtins { get; set; } = [];
    /// <summary>The variant text (size, colour), or null.</summary>
    public string? Variant { get; set; }
    public bool Discontinued { get; set; }
}

/// <summary>
/// <c>expected-returns</c> (warehouse PRD §4.3, Returns): a return the shop accepted and the warehouse should
/// expect, with a reason per line (commerce GA8) that sets the default disposition.
/// </summary>
[Replicated(SourceCollection = FulfilmentCollections.ExpectedReturns)]
public partial class FulfilmentExpectedReturn
{
    public string? Id { get; set; }
    public string OwnerId { get; set; } = "";
    /// <summary>The return number on the buyer's return label; scanning it starts the receipt.</summary>
    public string ReturnNumber { get; set; } = "";
    /// <summary>The <see cref="FulfilmentOrder"/> the goods were shipped for.</summary>
    public string FulfilmentOrderId { get; set; } = "";
    public List<FulfilmentExpectedReturnLine> Lines { get; set; } = [];
    public DateTimeOffset RequestedAt { get; set; }
}

/// <summary>A line of a <see cref="FulfilmentExpectedReturn"/>.</summary>
public sealed class FulfilmentExpectedReturnLine
{
    public string LineId { get; set; } = "";
    /// <summary>The <see cref="FulfilmentOrderLine.LineId"/> returned.</summary>
    public string OrderLineId { get; set; } = "";
    public string Sku { get; set; } = "";
    public decimal Quantity { get; set; }
    public FulfilmentReturnReason Reason { get; set; }
}

/// <summary>
/// <c>expected-receipts</c> (Q-W9, accepted): an advance shipping notice the shop or an ERP publishes, so receiving
/// can start from it instead of blind. Manual entry and CSV are the other sources.
/// </summary>
[Replicated(SourceCollection = FulfilmentCollections.ExpectedReceipts)]
public partial class FulfilmentExpectedReceipt
{
    public string? Id { get; set; }
    public string OwnerId { get; set; } = "";
    /// <summary>The ASN or purchase-order number on the delivery's paperwork.</summary>
    public string Reference { get; set; } = "";
    public string? Supplier { get; set; }
    /// <summary>The site the goods go to, by the warehouse's site code, or null for the owner's default site.</summary>
    public string? SiteCode { get; set; }
    /// <summary>The expected local delivery day, or null.</summary>
    public DateOnly? ExpectedOn { get; set; }
    public List<FulfilmentExpectedReceiptLine> Lines { get; set; } = [];
}

/// <summary>A line of a <see cref="FulfilmentExpectedReceipt"/>.</summary>
public sealed class FulfilmentExpectedReceiptLine
{
    public string LineId { get; set; } = "";
    public string Sku { get; set; } = "";
    public decimal Quantity { get; set; }
    public string? LotCode { get; set; }
    public DateOnly? Expiry { get; set; }
}
