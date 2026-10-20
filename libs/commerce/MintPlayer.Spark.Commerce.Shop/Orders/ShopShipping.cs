using System.Text;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Shipping;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Commerce.Shop.Contracts;
using MintPlayer.Spark.Commerce.Shop.Models;

namespace MintPlayer.Spark.Commerce.Shop.Orders;

/// <summary>The pure parts of shipping a shop order: which units, the booking request, the states that follow.</summary>
internal static class ShopShipping
{
    /// <summary>
    /// The units a shipment takes: the requested ones, capped at what is left to ship per line, or everything left when
    /// none are named. Cancelled lines never ship.
    /// </summary>
    public static List<ShopShippedLine> UnitsToShip(ShopOrder order, IReadOnlyList<SparkShopShipLine>? requested)
    {
        var left = order.Lines.Where(l => !l.Cancelled).ToDictionary(l => l.LineId, l => l.Quantity - l.ShippedQuantity, StringComparer.Ordinal);
        if (requested is not { Count: > 0 })
            return left.Where(l => l.Value > 0).Select(l => new ShopShippedLine { LineId = l.Key, Quantity = l.Value }).ToList();
        var units = new List<ShopShippedLine>();
        foreach (var group in requested.GroupBy(r => r.LineId, StringComparer.Ordinal))
        {
            if (!left.TryGetValue(group.Key, out var remaining) || remaining <= 0)
                continue;
            var quantity = Math.Min(remaining, group.Sum(r => Math.Max(0, r.Quantity)));
            if (quantity > 0)
                units.Add(new ShopShippedLine { LineId = group.Key, Quantity = quantity });
        }
        return units;
    }

    /// <summary>The state after a shipment: everything shipped, or part of it.</summary>
    public static EShopOrderState StateAfterShipping(ShopOrder order)
    {
        if (order.State is EShopOrderState.Delivered or EShopOrderState.Cancelled)
            return order.State;
        var open = order.Lines.Where(l => !l.Cancelled).ToList();
        if (open.Count > 0 && open.All(l => l.ShippedQuantity >= l.Quantity))
            return EShopOrderState.Shipped;
        return open.Any(l => l.ShippedQuantity > 0) ? EShopOrderState.PartiallyShipped : order.State;
    }

    /// <summary>
    /// The lines whose units are all delivered now that <paramref name="delivered"/> is (every outbound shipment holding
    /// the line is delivered and the whole quantity was shipped), not reported before.
    /// </summary>
    public static List<ShopOrderLine> NewlyDelivered(ShopOrder order, ShopOrderShipment delivered)
    {
        var lines = new List<ShopOrderLine>();
        foreach (var unit in delivered.Lines)
        {
            var line = order.Lines.FirstOrDefault(l => l.LineId == unit.LineId);
            if (line is null || line.DeliveredAt is not null || line.ShippedQuantity < line.Quantity)
                continue;
            var holding = order.Shipments.Where(s => !s.Return && s.Lines.Any(l => l.LineId == line.LineId)).ToList();
            if (holding.All(s => s.DeliveredAt is not null))
                lines.Add(line);
        }
        return lines;
    }

    /// <summary>The booking of a shipment (GB5 multi-parcel, GB6 customs lines, GC9 dangerous goods).</summary>
    public static CreateShipmentRequest Request(ShopOrder order, IReadOnlyList<ShopShippedLine> units, IReadOnlyList<SparkShopParcelRequest>? parcels,
        IReadOnlyDictionary<string, ShopVariant> variants, IReadOnlyDictionary<string, ShopProduct> products, SparkCommerceShopOptions options, string? actorId)
    {
        var customs = new List<CustomsItem>();
        var complete = true;
        var weight = 0;
        var dangerous = new List<DangerousGoodsItem>();
        var parcelLines = new List<ParcelLine>();
        foreach (var unit in units)
        {
            var line = order.Lines.First(l => l.LineId == unit.LineId);
            var variant = variants.GetValueOrDefault(line.VariantId);
            var product = variant is null ? null : products.GetValueOrDefault(variant.ProductId);
            var unitWeight = variant?.WeightGrams ?? options.Delivery.DefaultWeightGrams;
            weight += unitWeight * unit.Quantity;
            parcelLines.Add(new ParcelLine { Sku = line.Sku, Quantity = unit.Quantity });
            if (product?.DangerousGoods is { } dg)
                dangerous.Add(new DangerousGoodsItem { Class = dg, UnNumber = product.UnNumber });
            var hs = variant?.HsCode ?? product?.HsCode;
            var origin = variant?.OriginCountry ?? product?.OriginCountry;
            if (hs is null || origin is null || line.UnitPrice is null)
            {
                complete = false;
                continue;
            }
            customs.Add(new CustomsItem
            {
                Description = line.Title?.GetValue(TranslatedString.DefaultLanguage) ?? line.Sku,
                Quantity = unit.Quantity,
                WeightGrams = unitWeight * unit.Quantity,
                Value = line.UnitPrice * unit.Quantity,
                HsCode = hs,
                OriginCountry = origin,
                Sku = line.Sku,
            });
        }

        var requested = parcels is { Count: > 0 } ? parcels : [new SparkShopParcelRequest(Math.Max(1, weight))];
        var parcelRequests = requested.Select((p, i) => new ParcelRequest(Math.Max(1, p.WeightGrams), p.LengthMm, p.WidthMm, p.HeightMm)
        {
            Lines = i == 0 ? parcelLines : [],
        }).ToList();

        return new CreateShipmentRequest
        {
            Reference = order.Id!,
            Owner = order.OrganizationId,
            Method = order.Method,
            Carrier = order.Carrier,
            ServiceCode = order.ServiceCode,
            ShipTo = order.ShipTo!,
            ServicePoint = order.ServicePoint is { } point
                ? new ServicePoint { Id = point.Id, Carrier = order.Carrier ?? "", Name = point.Name, Kind = point.Kind, Address = point.Address }
                : null,
            Parcels = parcelRequests,
            // Shipping refuses a shipment leaving the EU customs territory without complete lines (GB6); partial lines are not sent.
            CustomsItems = complete && customs.Count > 0 ? customs : null,
            CustomsContentType = CustomsContentType.CommercialGoods,
            InvoiceNumber = order.OrderNumber,
            DangerousGoods = dangerous.Count > 0 ? dangerous : null,
            IdempotencyKey = Key("shop-ship", order.Id + "-" + (order.Shipments.Count(s => !s.Return) + 1)),
            CreatedBy = actorId,
        };
    }

    /// <summary>
    /// An idempotency key Shipping accepts (letters, digits, <c>-</c>, <c>_</c>, <c>/</c>; at most 120): the parts with
    /// every other character replaced, shortened with a hash when too long.
    /// </summary>
    public static string Key(string prefix, string value)
    {
        var builder = new StringBuilder(prefix).Append('/');
        foreach (var c in value)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '/' ? c : '_');
        var key = builder.ToString();
        if (key.Length <= 120)
            return key;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
        return prefix + "/" + hash;
    }

    /// <summary>The time of the latest tracking event whose status matches, as an instant.</summary>
    public static DateTimeOffset? EventTime(Shipment shipment, Func<TrackingStatus, bool> match)
    {
        var at = shipment.Events.Where(e => match(e.Status)).Select(e => (DateTime?)e.OccurredAtUtc).Max();
        return at is { } utc ? new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)) : null;
    }
}
