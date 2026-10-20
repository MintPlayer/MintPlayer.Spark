using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Fulfilment.Models;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Commerce.Shop.Catalog;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Fulfilment.Contracts;

namespace MintPlayer.Spark.Commerce.Fulfilment.Services;

/// <summary>
/// The shop's facts as the contracts carry them (warehouse §4.3): the order, the catalog item, the expected return. The
/// fields stay generic (lines, quantities, addresses); the shop's own documents are never published (WD2). Pure.
/// </summary>
internal static class FulfilmentContractBuilder
{
    /// <summary>The hold code of an order with an open chargeback (GB1): the warehouse keeps it unreleased.</summary>
    public const string DisputeHold = "dispute";

    /// <summary>
    /// The ship-to address as plain text (the warehouse needs no reference data): the buyer's address or, with a service
    /// point, the holder's name and the buyer's contact on the point's address, plus the point id. Null without an address.
    /// </summary>
    public static FulfilmentAddress? Address(ShopOrder shopOrder)
    {
        if (shopOrder.ShipTo is not { } to)
            return null;
        var street = shopOrder.ServicePoint?.Address is { Street.Length: > 0 } point ? point : to;
        return new FulfilmentAddress
        {
            Name = to.Name,
            Company = to.Company,
            Line1 = string.Join(" ", new[] { street.Street, street.HouseNumber, street.Addition }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim())),
            PostalCode = street.PostalCode,
            City = street.City,
            Region = street.Subdivision,
            CountryCode = street.Country.Trim().ToUpperInvariant(),
            Phone = to.Phone,
            Email = to.Email,
            ServicePointId = shopOrder.ServicePoint?.Id is { Length: > 0 } pointId ? pointId : null,
        };
    }

    /// <summary>A fulfilment order of <paramref name="lines"/> (Checkout line ids and units).</summary>
    public static FulfilmentOrder Order(string id, FulfilmentLink link, CheckoutOrder order, ShopOrder shopOrder, IReadOnlyList<FulfilmentLinkLine> lines,
        FulfilmentAddress address, IReadOnlyList<string> holds, SparkCommerceFulfilmentOptions options) => new()
    {
        Id = id,
        OwnerId = link.OwnerId,
        Channel = link.Channel,
        OrderNumber = order.Number,
        PlacedAt = order.ConfirmedAt ?? order.CreatedAt,
        Lines = lines.Select(l =>
        {
            var line = shopOrder.Lines.First(x => x.LineId == l.LineId);
            return new FulfilmentOrderLine { LineId = l.LineId, Sku = line.Sku, Quantity = l.Quantity, Name = line.Title?.ToString() };
        }).ToList(),
        ShipTo = address,
        CarrierCode = shopOrder.Carrier,
        ServiceCode = shopOrder.ServiceCode,
        ShipComplete = options.ShipComplete,
        BackorderPolicy = options.BackorderPolicy,
        Holds = [.. holds],
    };

    /// <summary>An expected return of <paramref name="lines"/> that left in fulfilment order <paramref name="fulfilmentOrderId"/>, with the reason per line (GA8).</summary>
    public static FulfilmentExpectedReturn ExpectedReturn(string id, FulfilmentLink link, string fulfilmentOrderId, CheckoutReturn ret, ShopOrder shopOrder,
        IReadOnlyList<CheckoutReturnLine> lines) => new()
    {
        Id = id,
        OwnerId = link.OwnerId,
        ReturnNumber = $"{link.OrderNumber}-{ret.ReturnId}",
        FulfilmentOrderId = fulfilmentOrderId,
        RequestedAt = ret.ReceivedAt,
        Lines = lines.Select(l => new FulfilmentExpectedReturnLine
        {
            LineId = l.LineId,
            OrderLineId = l.LineId,
            Sku = shopOrder.Lines.First(x => x.LineId == l.LineId).Sku,
            Quantity = l.Quantity,
            Reason = Reason(ret.Reason),
        }).ToList(),
    };

    /// <summary>Checkout's return reason (GA8) as the contract carries it; the names are the same taxonomy.</summary>
    public static FulfilmentReturnReason Reason(ReturnReasonCode reason) => reason switch
    {
        ReturnReasonCode.Withdrawal => FulfilmentReturnReason.Withdrawal,
        ReturnReasonCode.NonConformity => FulfilmentReturnReason.NonConformity,
        ReturnReasonCode.WrongItem => FulfilmentReturnReason.WrongItem,
        ReturnReasonCode.DamagedInTransit => FulfilmentReturnReason.DamagedInTransit,
        ReturnReasonCode.SellerAllowed => FulfilmentReturnReason.SellerAllowed,
        _ => FulfilmentReturnReason.Other,
    };

    /// <summary>
    /// The disposition Checkout records for a receipt (one per return): Restock only when every line was restocked,
    /// otherwise the line kept furthest from sale. For warehouse-backed lines Checkout restocks nothing either way (§4.4
    /// rule 4): the warehouse's restock raised its on-hand.
    /// </summary>
    public static ReturnDisposition Disposition(IEnumerable<FulfilmentReturnReceiptLine> lines)
    {
        var dispositions = lines.Select(l => l.Disposition).Distinct().ToList();
        if (dispositions.Count == 0 || dispositions.All(d => d == FulfilmentReturnDisposition.Restock))
            return ReturnDisposition.Restock;
        if (dispositions.Contains(FulfilmentReturnDisposition.Scrap) || dispositions.Contains(FulfilmentReturnDisposition.ReturnToVendor))
            return ReturnDisposition.Dispose;
        return ReturnDisposition.Quarantine;
    }

    /// <summary>A variant as the warehouse knows it (WD9): identity (owner, SKU), name, GTINs, variant text, discontinued.</summary>
    public static FulfilmentCatalogItem CatalogItem(string id, string ownerId, ShopProduct product, ShopVariant variant) => new()
    {
        Id = id,
        OwnerId = ownerId,
        Sku = variant.Sku,
        Name = product.Name.ToString(),
        Gtins = variant.Gtin is { Length: > 0 } gtin ? [gtin] : [],
        Variant = variant.Options.Count == 0 ? null : string.Join(" · ", variant.Options.Select(o => $"{o.Name}: {o.Value}")),
        // Kept published while discontinued (§4.5 Items): the warehouse may still hold stock of it.
        Discontinued = !variant.IsActive || !product.IsPublished,
    };

    /// <summary>Whether a stored catalog item already says what <paramref name="fresh"/> says.</summary>
    public static bool Same(FulfilmentCatalogItem stored, FulfilmentCatalogItem fresh)
        => stored.OwnerId == fresh.OwnerId && stored.Sku == fresh.Sku && stored.Name == fresh.Name && stored.Variant == fresh.Variant
            && stored.Discontinued == fresh.Discontinued && stored.Gtins.SequenceEqual(fresh.Gtins);

    /// <summary>Whether the shop's line is goods the warehouse ships.</summary>
    public static bool IsWarehouseLine(CheckoutOrderLine line, SparkCommerceFulfilmentOptions options)
        => line.IsPhysicalGoods && ShopItemIds.Handles(line.ItemId) && options.Ships(line.Channel);
}
