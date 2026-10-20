using MintPlayer.Spark.Fulfilment.Contracts;

namespace MintPlayer.Spark.Commerce.Fulfilment.Models;

/// <summary>
/// The shop's own bookkeeping of one shop order at the warehouse (never published, WD2): the fulfilment orders it
/// published for it (one per revision: an edit is a cancellation plus a new order), and which of the warehouse's facts it
/// already passed on to Shop and Checkout. Every decision is re-derived from the order, its refunds and the replicas
/// (<see cref="Services.FulfilmentPlanner"/>), so a redelivered or reordered fact changes nothing twice.
/// Id <see cref="FulfilmentIds.Link"/>.
/// </summary>
public sealed class FulfilmentLink
{
    public string? Id { get; set; }

    /// <summary>The shop's side of the order (<c>ShopOrders/{orderId}/{seller}</c>).</summary>
    public string ShopOrderId { get; set; } = "";

    /// <summary>Checkout's order.</summary>
    public string OrderId { get; set; } = "";

    public string OrderNumber { get; set; } = "";

    /// <summary>The seller; null for the platform's own sale.</summary>
    public string? SellerOrganizationId { get; set; }

    /// <summary>The owner of the goods at the warehouse (Q-W23).</summary>
    public string OwnerId { get; set; } = "";

    public string Channel { get; set; } = "";

    /// <summary>The fulfilment orders published for this shop order, oldest first.</summary>
    public List<FulfilmentRevision> Revisions { get; set; } = [];

    /// <summary>A ship-to address staff changed after the order was placed (an edit: cancellation plus a new order).</summary>
    public FulfilmentAddress? ShipToOverride { get; set; }

    /// <summary>Shipments (<c>{fulfilmentOrderId}/shipments/{n}</c>) already reported to Shop as shipped.</summary>
    public List<string> ShipmentsReported { get; set; } = [];

    /// <summary>Shipments already reported to Shop as delivered.</summary>
    public List<string> DeliveriesReported { get; set; } = [];

    /// <summary>Checkout's returns published as expected returns.</summary>
    public List<FulfilmentLinkReturn> Returns { get; set; } = [];

    /// <summary>Return receipts already registered with Checkout.</summary>
    public List<string> ReceiptsReported { get; set; } = [];

    /// <summary>What staff must look at, or null.</summary>
    public FulfilmentAttention? Attention { get; set; }

    /// <summary>Nothing left to derive: the reconcile skips it; an order event or a replica change re-opens it.</summary>
    public bool Closed { get; set; }

    public DateTimeOffset? LastShippedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the reconcile last looked at it (the reconcile takes the least recently checked first).</summary>
    public DateTimeOffset CheckedAt { get; set; }
}

/// <summary>One published fulfilment order of a <see cref="FulfilmentLink"/>.</summary>
public sealed class FulfilmentRevision
{
    /// <summary>The <see cref="FulfilmentOrder"/> id (<see cref="FulfilmentIds.Order"/>).</summary>
    public string Id { get; set; } = "";

    public int Number { get; set; }

    /// <summary>The quantities asked for, by Checkout line id.</summary>
    public List<FulfilmentLinkLine> Lines { get; set; } = [];

    public DateTimeOffset PublishedAt { get; set; }

    /// <summary>When the shop asked the warehouse to cancel it (the whole order: one cancellation per revision).</summary>
    public DateTimeOffset? CancellationRequestedAt { get; set; }

    /// <summary>Staff acknowledged its rejection (or refused cancellation): a rejected order no longer blocks a new one.</summary>
    public bool Acknowledged { get; set; }
}

public sealed class FulfilmentLinkLine
{
    public string LineId { get; set; } = "";

    public int Quantity { get; set; }
}

/// <summary>A Checkout return published as one or more <see cref="FulfilmentExpectedReturn"/>s (one per fulfilment order the goods left in).</summary>
public sealed class FulfilmentLinkReturn
{
    public string ReturnId { get; set; } = "";

    public List<string> ExpectedReturnIds { get; set; } = [];

    public DateTimeOffset PublishedAt { get; set; }

    /// <summary>The withheld refund was released at once (<see cref="FulfilmentRefundTiming.OnRequest"/>).</summary>
    public DateTimeOffset? ReleasedAt { get; set; }

    /// <summary>The warehouse's first receipt for it was registered with Checkout.</summary>
    public DateTimeOffset? ReceivedAt { get; set; }
}

/// <summary>Why staff should look at an order.</summary>
public enum FulfilmentAttentionKind
{
    /// <summary>The warehouse refused the order at intake; no new order is published until staff acknowledge it.</summary>
    Rejected,
    /// <summary>The warehouse refused a cancellation (already shipped): the refunded goods reach the buyer; turn it into a return.</summary>
    CancellationRefused,
    /// <summary>
    /// The warehouse cancelled units it cannot ship (<see cref="FulfilmentBackorderPolicy.CancelRemainder"/>) and their
    /// automatic refund did not go through (Checkout refused it, or the provider failed it): staff retry or settle it.
    /// </summary>
    Undeliverable,
    /// <summary>The order has no ship-to address yet.</summary>
    MissingAddress,
}

public sealed class FulfilmentAttention
{
    public FulfilmentAttentionKind Kind { get; set; }

    /// <summary>The fulfilment order concerned, when one is.</summary>
    public string? FulfilmentOrderId { get; set; }

    /// <summary>A text for people: the rejection reason and detail, the lines concerned.</summary>
    public string? Detail { get; set; }

    public DateTimeOffset At { get; set; }
}

/// <summary>
/// A variant the shop published as a <see cref="FulfilmentCatalogItem"/>: maps the warehouse's (owner, SKU) back to the
/// variant whose quota the stock feed sets (§4.4), with the onboarding baseline (rule 2). Id <see cref="FulfilmentIds.Sku"/>.
/// </summary>
public sealed class FulfilmentSkuLink
{
    public string? Id { get; set; }

    public string OwnerId { get; set; } = "";

    public string Sku { get; set; } = "";

    public string VariantId { get; set; } = "";

    public string ProductId { get; set; } = "";

    /// <summary>The channel the variant sells in (its product's).</summary>
    public string Channel { get; set; } = "";

    public string CatalogItemId { get; set; } = "";

    /// <summary>
    /// §4.4 rule 2: units this shop sold and shipped itself before the warehouse took over. <c>sold</c> counts them and the
    /// warehouse's <c>ShippedTotal</c> does not, so the capacity adds them back: both then share one epoch.
    /// </summary>
    public int Baseline { get; set; }

    /// <summary>The <see cref="FulfilmentStockLevel.Version"/> last applied; an older snapshot is ignored.</summary>
    public long? AppliedVersion { get; set; }

    /// <summary>The capacity last asked of the quota.</summary>
    public int? AppliedCapacity { get; set; }

    public DateTimeOffset? AppliedAt { get; set; }
}

/// <summary>A recall notice sent (GA7): who was mailed, so a repeated send mails nobody twice. Id <see cref="FulfilmentIds.Recall"/>.</summary>
public sealed class FulfilmentRecall
{
    public string? Id { get; set; }

    public string Sku { get; set; } = "";

    public string LotCode { get; set; } = "";

    public string? OwnerId { get; set; }

    public List<string> NotifiedOrderIds { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The ids of the published contracts and of the shop's bookkeeping. Every published id starts with the shop's name, so a
/// warehouse serving several shops never sees two shops use one id (commerce §4.9, warehouse §4.3).
/// </summary>
public static class FulfilmentIds
{
    public const string PlatformSeller = "platform";

    /// <summary>The seller segment of an id: the organization id, or <c>platform</c>.</summary>
    public static string SellerKey(string? sellerOrganizationId) => sellerOrganizationId ?? PlatformSeller;

    private static string Base(string shop, string orderNumber, string? seller) => $"{shop}/{orderNumber}/{SellerKey(seller)}";

    /// <summary>A published fulfilment order: <c>FulfilmentOrders/{shop}/{orderNumber}/{seller}/{revision}</c>.</summary>
    public static string Order(string shop, string orderNumber, string? seller, int revision)
        => $"{FulfilmentCollections.Orders}/{Base(shop, orderNumber, seller)}/{revision}";

    /// <summary>The shop's bookkeeping of a shop order: <c>FulfilmentLinks/{shop}/{orderNumber}/{seller}</c>.</summary>
    public static string Link(string shop, string orderNumber, string? seller) => $"FulfilmentLinks/{Base(shop, orderNumber, seller)}";

    /// <summary>The warehouse's status of a fulfilment order (warehouse §4.3).</summary>
    public static string Status(string fulfilmentOrderId) => fulfilmentOrderId + "/status";

    /// <summary>The prefix of a fulfilment order's shipments (<c>{fulfilmentOrderId}/shipments/{n}</c>).</summary>
    public static string ShipmentsPrefix(string fulfilmentOrderId) => fulfilmentOrderId + "/shipments/";

    /// <summary>An expected return: <c>FulfilmentExpectedReturns/{shop}/{orderNumber}/{seller}/{returnId}/{revision}</c>.</summary>
    public static string ExpectedReturn(string shop, string orderNumber, string? seller, string returnId, int revision)
        => $"{FulfilmentCollections.ExpectedReturns}/{Base(shop, orderNumber, seller)}/{returnId}/{revision}";

    /// <summary>A catalog item: <c>FulfilmentCatalogItems/{shop}/{owner}/{sku}</c>.</summary>
    public static string CatalogItem(string shop, string ownerId, string sku) => $"{FulfilmentCollections.CatalogItems}/{shop}/{ownerId}/{sku}";

    /// <summary>The shop's map from the warehouse's (owner, SKU) to its variant.</summary>
    public static string Sku(string ownerId, string sku) => $"FulfilmentSkus/{ownerId}/{sku}";

    public static string Recall(string recallId) => $"FulfilmentRecalls/{recallId}";

    /// <summary>The link of one of this shop's fulfilment orders, or null for an id this shop did not publish.</summary>
    public static string? LinkOfOrder(string shop, string? fulfilmentOrderId)
        => Strip(shop, fulfilmentOrderId, FulfilmentCollections.Orders, segments: 1);

    /// <summary>The link of one of this shop's expected returns, or null for an id this shop did not publish.</summary>
    public static string? LinkOfExpectedReturn(string shop, string? expectedReturnId)
        => Strip(shop, expectedReturnId, FulfilmentCollections.ExpectedReturns, segments: 2);

    // The seller segment may contain '/' (an organization id), so the link is found by dropping trailing segments.
    private static string? Strip(string shop, string? id, string collection, int segments)
    {
        var prefix = $"{collection}/{shop}/";
        if (id is null || !id.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var rest = id[(collection.Length + 1)..];
        for (var i = 0; i < segments; i++)
        {
            var slash = rest.LastIndexOf('/');
            if (slash <= shop.Length)
                return null;
            rest = rest[..slash];
        }
        // {shop}/{orderNumber}/{seller}: at least three segments.
        return rest.Count(c => c == '/') >= 2 ? "FulfilmentLinks/" + rest : null;
    }
}
