using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Shipping;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Commerce.Shop.Catalog;
using MintPlayer.Spark.Commerce.Shop.Contracts;
using MintPlayer.Spark.Commerce.Shop.Delivery;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Commerce.Shop.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Commerce.Shop.Orders;

/// <summary>
/// The shop's orders (PRD §4.10 "Paid → Shipped → Delivered"): the shop's side of each checkout order with goods, one per
/// seller. Staff ship (ShopOnly) and inspect returns here. <b>The fulfilment seam (D13):</b> in Warehouse mode the
/// warehouse's shop side (Commerce.Fulfilment over Fulfilment.Contracts) reports through
/// <see cref="MarkShippedAsync"/> and <see cref="MarkDeliveredAsync"/>, sets stock through <see cref="ISparkShopStock"/>,
/// and reports goods back straight to Checkout's <c>ISparkCheckoutRefunds.RegisterGoodsBackAsync</c> (the warehouse
/// receipt and restock); Shop then never books a shipment or a return label.
/// </summary>
public interface ISparkShopOrders
{
    /// <summary>The shop's side of <paramref name="orderId"/> for one seller (null: the platform), or null.</summary>
    Task<ShopOrder?> GetAsync(string orderId, string? sellerOrganizationId, CancellationToken cancellationToken = default);

    /// <summary>The caller's own order by number, as the order page shows it (one entry per seller); null when it is not theirs.</summary>
    Task<IReadOnlyList<SparkShopOrder>?> GetOwnAsync(string orderNumber, CancellationToken cancellationToken = default);

    /// <summary>ShopOnly: books a shipment of some (or all remaining) units with one label per parcel (GB5) and marks them shipped.</summary>
    Task<SparkShopShipResult> ShipAsync(SparkShopShipRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Units left the building (the warehouse's shipment, or a host's own carrier): Checkout is told per line
    /// (<c>MarkShippedAsync</c>: a later refund returns no quota until restock, I4). Idempotent per <paramref name="reference"/>.
    /// </summary>
    Task MarkShippedAsync(string orderId, string? sellerOrganizationId, IReadOnlyList<ShopShippedLine> lines, DateTimeOffset at, string reference, CancellationToken cancellationToken = default);

    /// <summary>
    /// The buyer received the units of <paramref name="reference"/>: Checkout is told per line whose units are all
    /// delivered (<c>MarkDeliveredAsync</c>: the withdrawal and guarantee periods start, CRD Art. 9(2)(b)).
    /// </summary>
    Task MarkDeliveredAsync(string orderId, string? sellerOrganizationId, string reference, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>The disposition of returned goods (GA8), with the receipt when it was not registered yet.</summary>
    Task InspectAsync(SparkShopInspectRequest request, CancellationToken cancellationToken = default);

    /// <summary>ShopOnly: books a return label for an expected return and mails it to the buyer (GB5). Idempotent per return.</summary>
    Task<string> CreateReturnLabelAsync(SparkShopReturnLabelRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The warehouse's stock feed into the shop's quotas (D13, PRD §4.9 "Stock feed").</summary>
public interface ISparkShopStock
{
    /// <summary>
    /// Sets a variant's quota capacity (<c>capacity = OnHandSellable − ReservedOtherChannels − buffer + ShippedTotal</c>,
    /// computed by the feed, so late or repeated updates keep I1 exact). Creates the quota when missing. Refused below
    /// what is sold and held (answers false).
    /// </summary>
    Task<bool> SetStockAsync(string variantId, int capacity, CancellationToken cancellationToken = default);
}

internal sealed class ShopOrders(
    IDocumentStore store,
    ISparkCheckout checkout,
    ISparkCheckoutRefunds refunds,
    ISparkShipping shipping,
    ISparkInventory inventory,
    ShopAccess access,
    ShopReturnMails mails,
    IOptionsMonitor<SparkCommerceShopOptions> options,
    TimeProvider clock,
    ILogger<ShopOrders> logger) : ISparkShopOrders, ISparkShopStock
{
    // ---- reads -----------------------------------------------------------------------------------------------------

    public async Task<ShopOrder?> GetAsync(string orderId, string? sellerOrganizationId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<ShopOrder>(ShopDocumentIds.Order(orderId, sellerOrganizationId), cancellationToken);
    }

    public async Task<IReadOnlyList<SparkShopOrder>?> GetOwnAsync(string orderNumber, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderNumber) || await checkout.GetOwnOrderAsync(orderNumber, cancellationToken) is not { Id: { } orderId })
            return null;
        using var session = store.OpenAsyncSession();
        var shopOrders = await session.Query<ShopOrder>().Customize(c => c.WaitForNonStaleResults()).Where(o => o.OrderId == orderId).Take(64).ToListAsync(cancellationToken);
        var shipmentIds = shopOrders.SelectMany(o => o.Shipments).Select(s => s.ShipmentId).Distinct(StringComparer.Ordinal).ToList();
        Dictionary<string, Shipment> shipments = shipmentIds.Count == 0 ? new() : await session.LoadAsync<Shipment>(shipmentIds, cancellationToken);
        var window = options.CurrentValue.Reviews.Window;
        var now = clock.GetUtcNow();
        return shopOrders.Select(o => new SparkShopOrder(
            o.OrderNumber, o.OrganizationId, o.State.ToString(), o.Method, o.ServicePoint?.Name, o.MinimumAge,
            o.Lines.Select(l => new SparkShopOrderLine(l.LineId, l.ItemId, l.ProductId, l.Title, l.Quantity, l.ShippedQuantity, l.DeliveredAt,
                l.DeliveredAt is { } delivered && now - delivered <= window && options.CurrentValue.Reviews.Enabled)).ToList(),
            o.Shipments.Select(s => shipments.TryGetValue(s.ShipmentId, out var shipment) && shipment is not null ? View(shipment) : null).OfType<SparkShopShipment>().ToList(),
            o.ShippedAt, o.DeliveredAt)).ToList();
    }

    private static SparkShopShipment View(Shipment shipment)
        => new(shipment.Direction == ShipmentDirection.Return, shipment.Carrier, shipment.Status.ToString(),
            shipment.Parcels.Where(p => !p.Voided).Select(p => new SparkShopParcel(p.Number, p.TrackingNumber, p.TrackingUrl, p.Status.ToString())).ToList(),
            shipment.PromisedBy);

    // ---- creation from checkout's order events --------------------------------------------------------------------

    /// <summary>
    /// Creates the shop's side of <paramref name="order"/> once per seller with goods (idempotent: an atomic natural-id
    /// create, X3): the delivery the buyer chose (from the order's delivery extra and its choice document) and the least
    /// expensive standard delivery offered at that moment (CRD Art. 13(2), GA1).
    /// </summary>
    internal async Task EnsureAsync(CheckoutOrder order, CancellationToken cancellationToken)
    {
        var groups = order.Lines.Where(l => l.IsPhysicalGoods && ShopItemIds.Handles(l.ItemId))
            .GroupBy(l => l.SellerOrganizationId ?? "", StringComparer.Ordinal).ToList();
        if (groups.Count == 0)
            return;
        using var session = store.OpenAsyncSession();
        var shopExtras = order.Extras.Where(e => e.Source == ShopChoices.Source && e.Code.StartsWith(ShopChoices.DeliveryExtraPrefix, StringComparison.Ordinal)).ToList();
        foreach (var group in groups)
        {
            var seller = group.Key.Length == 0 ? null : group.Key;
            var id = ShopDocumentIds.Order(order.Id!, seller);
            if (await session.LoadAsync<ShopOrder>(id, cancellationToken) is not null)
                continue;

            var extra = shopExtras.Count == 1 ? shopExtras[0] : shopExtras.FirstOrDefault(e => e.Code.EndsWith(":" + (seller ?? "platform"), StringComparison.Ordinal));
            var choice = extra?.Reference is { Length: > 0 } choiceId ? await session.LoadAsync<ShopDeliveryChoice>(choiceId, cancellationToken) : null;
            var variantIds = group.Select(l => ShopItemIds.VariantId(l.ItemId)!).Distinct(StringComparer.Ordinal).ToList();
            var variants = await session.Include<ShopVariant>(v => v.ProductId).LoadAsync<ShopVariant>(variantIds, cancellationToken);
            var products = await session.LoadAsync<ShopProduct>(variants.Values.OfType<ShopVariant>().Select(v => v.ProductId).Distinct(StringComparer.Ordinal), cancellationToken);

            var shopOrder = new ShopOrder
            {
                Id = id,
                OrganizationId = seller,
                OrderId = order.Id!,
                OrderNumber = order.Number,
                Fulfilment = options.CurrentValue.Fulfilment,
                Currency = order.Currency,
                ShipTo = choice?.ShipTo,
                Method = choice?.Method,
                ServicePoint = choice?.ServicePoint,
                DeliveryCost = extra?.Amount,
                DeliveryExtraCode = extra?.Code,
                MinimumAge = group.Max(l => l.MinimumAge),
                CreatedAt = clock.GetUtcNow(),
                UpdatedAt = clock.GetUtcNow(),
                Lines = group.Select(l =>
                {
                    var variant = variants.GetValueOrDefault(ShopItemIds.VariantId(l.ItemId)!);
                    return new ShopOrderLine
                    {
                        LineId = l.LineId,
                        ItemId = l.ItemId,
                        VariantId = variant?.Id ?? ShopItemIds.VariantId(l.ItemId)!,
                        ProductId = variant?.ProductId ?? "",
                        Sku = variant?.Sku ?? "",
                        Title = l.Title,
                        Quantity = l.Quantity,
                        UnitPrice = l.UnitPrice,
                    };
                }).ToList(),
            };

            if (choice is not null)
            {
                var weight = group.Sum(l => (variants.GetValueOrDefault(ShopItemIds.VariantId(l.ItemId)!)?.WeightGrams ?? options.CurrentValue.Delivery.DefaultWeightGrams) * l.Quantity);
                var dangerous = products.Values.OfType<ShopProduct>().Where(p => p.DangerousGoods is not null)
                    .Select(p => new DangerousGoodsItem { Class = p.DangerousGoods!.Value, UnNumber = p.UnNumber }).ToList();
                var parcel = new DeliveryParcel(seller, Money.Sum(group.Select(l => l.Total), order.Currency), weight, dangerous);
                try
                {
                    var rates = await shipping.GetRatesAsync(DeliveryMath.RateRequest(choice.ShipTo, parcel), cancellationToken);
                    var chosen = rates.FirstOrDefault(r => string.Equals(r.Method, choice.Method, StringComparison.OrdinalIgnoreCase));
                    shopOrder.Carrier = chosen?.Carrier;
                    shopOrder.ServiceCode = chosen?.ServiceCode;
                    shopOrder.StandardDeliveryCost = StandardDelivery(rates, extra?.Amount);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The standard cost then falls back to Checkout's default (all of the delivery counts as standard).
                    logger.LogWarning(ex, "Rates for order {OrderId} could not be read; the standard delivery cost is not recorded.", order.Id);
                }
            }

            try
            {
                await session.StoreAsync(shopOrder, string.Empty, id, cancellationToken);
                await session.SaveChangesAsync(cancellationToken);
            }
            catch (ConcurrencyException)
            {
                // Another delivery of the event created it first.
                session.Advanced.Evict(shopOrder);
            }
        }
    }

    /// <summary>
    /// GA1, CRD Art. 13(2): the least expensive delivery offered (carrier methods; collecting in person is not a delivery),
    /// never more than what the buyer paid. Pure.
    /// </summary>
    internal static Money? StandardDelivery(IReadOnlyList<ShippingRate> rates, Money? paid)
    {
        var cheapest = rates.Where(r => r.Kind == ShippingMethodKind.Carrier && (paid is null || r.Price.Currency == paid.Currency))
            .Select(r => r.Price).OrderBy(p => p.ToMinorUnits()).FirstOrDefault();
        if (cheapest is null)
            return paid;
        return paid is not null && paid < cheapest ? paid : cheapest;
    }

    /// <summary>Applies an order event to the shop's orders of <paramref name="order"/>: payment, cancellation, refused or returned lines.</summary>
    internal async Task ApplyOrderStateAsync(CheckoutOrder order, OrderEventKind kind, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var orderId = order.Id!;
        var shopOrders = await session.Query<ShopOrder>().Customize(c => c.WaitForNonStaleResults()).Where(o => o.OrderId == orderId).Take(64).ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        foreach (var shopOrder in shopOrders)
        {
            foreach (var line in shopOrder.Lines)
            {
                var orderLine = order.Lines.FirstOrDefault(l => l.LineId == line.LineId);
                // A refused line never ships; a line returned before it shipped (an early withdrawal) neither.
                if (orderLine is { Status: OrderLineStatus.Refused } || (orderLine is { Status: OrderLineStatus.Returned } && line.ShippedQuantity == 0))
                    line.Cancelled = true;
            }
            switch (kind)
            {
                case OrderEventKind.Confirmed when shopOrder.State == EShopOrderState.AwaitingPayment:
                    shopOrder.State = EShopOrderState.Paid;
                    shopOrder.PaidAt ??= now;
                    break;
                case OrderEventKind.Cancelled or OrderEventKind.Expired or OrderEventKind.Voided when shopOrder.State == EShopOrderState.AwaitingPayment:
                    shopOrder.State = EShopOrderState.Cancelled;
                    break;
            }
            if (shopOrder.Lines.All(l => l.Cancelled) && shopOrder.State is EShopOrderState.Paid)
                shopOrder.State = EShopOrderState.Cancelled;
            shopOrder.UpdatedAt = now;
        }
        await session.SaveChangesAsync(cancellationToken);
    }

    // ---- shipping (ShopOnly) ---------------------------------------------------------------------------------------

    public async Task<SparkShopShipResult> ShipAsync(SparkShopShipRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await access.MayHandleOrdersAsync(request.OrganizationId, cancellationToken))
            throw ShopException.Of(ShopRefusals.Forbidden);
        using var session = store.OpenAsyncSession();
        var shopOrder = await session.LoadAsync<ShopOrder>(ShopDocumentIds.Order(request.OrderId, request.OrganizationId), cancellationToken)
            ?? throw ShopException.Of(ShopRefusals.OrderNotFound);
        if (shopOrder.Fulfilment == ShopFulfilmentMode.Warehouse)
            throw ShopException.Of(ShopRefusals.WarehouseShips);
        if (shopOrder.State is not (EShopOrderState.Paid or EShopOrderState.PartiallyShipped) || shopOrder.ShipTo is null)
            throw ShopException.Of(ShopRefusals.NothingToShip);

        var units = ShopShipping.UnitsToShip(shopOrder, request.Lines);
        if (units.Count == 0)
            throw ShopException.Of(ShopRefusals.NothingToShip);

        var variantIds = units.Select(u => shopOrder.Lines.First(l => l.LineId == u.LineId).VariantId).Distinct(StringComparer.Ordinal).ToList();
        var variants = await session.Include<ShopVariant>(v => v.ProductId).LoadAsync<ShopVariant>(variantIds, cancellationToken);
        var products = await session.LoadAsync<ShopProduct>(variants.Values.OfType<ShopVariant>().Select(v => v.ProductId).Distinct(StringComparer.Ordinal), cancellationToken);
        var shipment = await shipping.CreateShipmentAsync(
            ShopShipping.Request(shopOrder, units, request.Parcels, variants, products, options.CurrentValue, access.UserId),
            cancellationToken);

        shopOrder.Shipments.Add(new ShopOrderShipment { ShipmentId = shipment.Id!, Lines = units, LabelledAt = clock.GetUtcNow() });
        await ApplyShippedAsync(shopOrder, units, clock.GetUtcNow(), cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return new SparkShopShipResult(shipment.Id!, shipment.Parcels.Where(p => !p.Voided).Select(p => p.TrackingNumber).OfType<string>().ToList());
    }

    public async Task MarkShippedAsync(string orderId, string? sellerOrganizationId, IReadOnlyList<ShopShippedLine> lines, DateTimeOffset at, string reference, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var shopOrder = await session.LoadAsync<ShopOrder>(ShopDocumentIds.Order(orderId, sellerOrganizationId), cancellationToken)
            ?? throw ShopException.Of(ShopRefusals.OrderNotFound);
        if (shopOrder.Shipments.Any(s => s.ShipmentId == reference))
            return; // idempotent per reference
        var units = ShopShipping.UnitsToShip(shopOrder, lines.Select(l => new SparkShopShipLine(l.LineId, l.Quantity)).ToList());
        shopOrder.Shipments.Add(new ShopOrderShipment { ShipmentId = reference, Lines = units, LabelledAt = at });
        await ApplyShippedAsync(shopOrder, units, at, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task ApplyShippedAsync(ShopOrder shopOrder, IReadOnlyList<ShopShippedLine> units, DateTimeOffset at, CancellationToken cancellationToken)
    {
        foreach (var unit in units)
        {
            var line = shopOrder.Lines.First(l => l.LineId == unit.LineId);
            line.ShippedQuantity = Math.Min(line.Quantity, line.ShippedQuantity + unit.Quantity);
            line.ShippedAt ??= at;
            // I4: a refund after shipment returns no quota until restock.
            await checkout.MarkShippedAsync(shopOrder.OrderId, line.LineId, at, cancellationToken);
        }
        shopOrder.State = ShopShipping.StateAfterShipping(shopOrder);
        if (shopOrder.State == EShopOrderState.Shipped)
            shopOrder.ShippedAt ??= at;
        shopOrder.UpdatedAt = clock.GetUtcNow();
    }

    // ---- delivery --------------------------------------------------------------------------------------------------

    public async Task MarkDeliveredAsync(string orderId, string? sellerOrganizationId, string reference, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var shopOrder = await session.LoadAsync<ShopOrder>(ShopDocumentIds.Order(orderId, sellerOrganizationId), cancellationToken)
            ?? throw ShopException.Of(ShopRefusals.OrderNotFound);
        await ApplyDeliveredAsync(shopOrder, reference, at, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Marks a shipment delivered; every line whose units are now all delivered is reported to Checkout once.</summary>
    internal async Task ApplyDeliveredAsync(ShopOrder shopOrder, string shipmentId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var shipment = shopOrder.Shipments.FirstOrDefault(s => s.ShipmentId == shipmentId && !s.Return);
        if (shipment is null || shipment.DeliveredAt is not null)
            return;
        shipment.DeliveredAt = at;
        foreach (var line in ShopShipping.NewlyDelivered(shopOrder, shipment))
        {
            line.DeliveredAt = at;
            // CRD Art. 9(2)(b): the withdrawal period counts from the day after the last goods were received; the
            // guarantee (Directive (EU) 2019/771 Art. 10(1)) from delivery.
            await refunds.MarkDeliveredAsync(shopOrder.OrderId, line.LineId, at, cancellationToken);
        }
        if (shopOrder.Lines.Where(l => !l.Cancelled).All(l => l.DeliveredAt is not null))
        {
            shopOrder.State = EShopOrderState.Delivered;
            shopOrder.DeliveredAt ??= at;
        }
        shopOrder.UpdatedAt = clock.GetUtcNow();
    }

    // ---- returns ---------------------------------------------------------------------------------------------------

    /// <summary>Records a return Checkout expects (GA1/GA8) on the seller's shop order; books and mails its label (ShopOnly).</summary>
    internal async Task OnReturnExpectedAsync(CheckoutOrder order, OrderReturnExpected expected, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var orderId = order.Id!;
        var shopOrders = await session.Query<ShopOrder>().Customize(c => c.WaitForNonStaleResults()).Where(o => o.OrderId == orderId).Take(64).ToListAsync(cancellationToken);
        var toLabel = new List<ShopOrder>();
        foreach (var shopOrder in shopOrders)
        {
            var lines = expected.Lines.Where(l => shopOrder.Lines.Any(x => x.LineId == l.LineId)).Select(l => new ShopShippedLine { LineId = l.LineId, Quantity = l.Quantity }).ToList();
            if (lines.Count == 0 || shopOrder.Returns.Any(r => r.ReturnId == expected.ReturnId))
                continue;
            shopOrder.Returns.Add(new ShopOrderReturn
            {
                ReturnId = expected.ReturnId,
                Reason = expected.Reason,
                DefaultDisposition = expected.DefaultDisposition,
                SellerPaysReturnShipping = expected.SellerPaysReturnShipping,
                Lines = lines,
            });
            shopOrder.UpdatedAt = clock.GetUtcNow();
            if (shopOrder.Fulfilment == ShopFulfilmentMode.ShopOnly && options.CurrentValue.Returns.AutoReturnLabel)
                toLabel.Add(shopOrder);
        }
        await session.SaveChangesAsync(cancellationToken);
        foreach (var shopOrder in toLabel)
        {
            try
            {
                await LabelReturnAsync(shopOrder.Id!, expected.ReturnId, null, cancellationToken);
            }
            catch (Exception ex) when (ex is ShippingRequestException or CarrierException or ShopException)
            {
                // Staff can book it later (POST …/staff/return-label); the return itself is recorded.
                logger.LogWarning(ex, "The return label of return {ReturnId} (order {OrderId}) could not be booked.", expected.ReturnId, order.Id);
            }
        }
    }

    public async Task<string> CreateReturnLabelAsync(SparkShopReturnLabelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await access.MayHandleOrdersAsync(request.OrganizationId, cancellationToken))
            throw ShopException.Of(ShopRefusals.Forbidden);
        return await LabelReturnAsync(ShopDocumentIds.Order(request.OrderId, request.OrganizationId), request.ReturnId, access.UserId, cancellationToken);
    }

    private async Task<string> LabelReturnAsync(string shopOrderId, string returnId, string? actorId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var shopOrder = await session.LoadAsync<ShopOrder>(shopOrderId, cancellationToken) ?? throw ShopException.Of(ShopRefusals.OrderNotFound);
        if (shopOrder.Fulfilment == ShopFulfilmentMode.Warehouse)
            throw ShopException.Of(ShopRefusals.WarehouseShips);
        var ret = shopOrder.Returns.FirstOrDefault(r => r.ReturnId == returnId) ?? throw ShopException.Of(ShopRefusals.ReturnNotFound, returnId);
        if (ret.ReturnShipmentId is { } existing)
            return existing;
        var outbound = shopOrder.Shipments.LastOrDefault(s => !s.Return && s.Lines.Any(l => ret.Lines.Any(r => r.LineId == l.LineId)))
            ?? throw ShopException.Of(ShopRefusals.NothingToShip, returnId);
        var label = await shipping.CreateReturnLabelAsync(new ReturnLabelRequest
        {
            ShipmentId = outbound.ShipmentId,
            Reason = ret.Reason.ToString(),
            IdempotencyKey = ShopShipping.Key("shop-return", returnId),
            CreatedBy = actorId,
        }, cancellationToken);
        ret.ReturnShipmentId = label.Id;
        shopOrder.Shipments.Add(new ShopOrderShipment { ShipmentId = label.Id!, Return = true, ReturnId = returnId, Lines = ret.Lines, LabelledAt = clock.GetUtcNow() });
        if (ret.LabelMailedAt is null && await mails.SendReturnLabelAsync(shopOrder, ret, label, cancellationToken))
            ret.LabelMailedAt = clock.GetUtcNow();
        shopOrder.UpdatedAt = clock.GetUtcNow();
        await session.SaveChangesAsync(cancellationToken);
        return label.Id!;
    }

    /// <summary>
    /// A return shipment's tracking: the carrier's first scan is the buyer's proof of dispatch (CRD Art. 13(3): a
    /// withheld refund is paid now); its delivery to the seller registers the receipt when <c>Returns:ReceiptOn</c> is
    /// <c>CarrierDelivery</c>, with the <c>Inspect</c> disposition (staff decide restock or quarantine at inspection).
    /// </summary>
    internal async Task OnReturnTrackingAsync(ShopOrder shopOrder, Shipment shipment, bool proofOfDispatch, CancellationToken cancellationToken)
    {
        var ret = shopOrder.Returns.FirstOrDefault(r => r.ReturnShipmentId == shipment.Id)
            ?? shopOrder.Returns.FirstOrDefault(r => r.ReturnShipmentId is null && shipment.ReturnOf is not null && shopOrder.Shipments.Any(s => s.ShipmentId == shipment.ReturnOf));
        if (ret is null)
            return;
        ret.ReturnShipmentId ??= shipment.Id;
        var now = clock.GetUtcNow();
        if (proofOfDispatch && ret.ProofOfDispatchAt is null)
        {
            ret.ProofOfDispatchAt = ShopShipping.EventTime(shipment, s => s is not TrackingStatus.Pending and not TrackingStatus.Labelled) ?? now;
            await refunds.RegisterGoodsBackAsync(new GoodsBackRegistration(shopOrder.OrderId, ret.ReturnId, GoodsBackKind.ProofOfDispatch, ret.ProofOfDispatchAt.Value), cancellationToken);
        }
        if (shipment.Status == TrackingStatus.Delivered && ret.ArrivedAt is null)
        {
            ret.ArrivedAt = ShopShipping.EventTime(shipment, s => s == TrackingStatus.Delivered) ?? now;
            if (options.CurrentValue.Returns.ReceiptOn == ShopReceiptMoment.CarrierDelivery && ret.ReceivedAt is null)
            {
                ret.ReceivedAt = ret.ArrivedAt;
                await refunds.RegisterGoodsBackAsync(new GoodsBackRegistration(shopOrder.OrderId, ret.ReturnId, GoodsBackKind.Received, ret.ArrivedAt.Value, ReturnDisposition.Inspect), cancellationToken);
            }
        }
        shopOrder.UpdatedAt = now;
    }

    public async Task InspectAsync(SparkShopInspectRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await access.MayHandleOrdersAsync(request.OrganizationId, cancellationToken))
            throw ShopException.Of(ShopRefusals.Forbidden);
        if (!Enum.TryParse<ReturnDisposition>(request.Disposition, ignoreCase: false, out var disposition) || !Enum.IsDefined(disposition))
            throw ShopException.Of(ShopRefusals.ReturnNotFound, request.Disposition);
        using var session = store.OpenAsyncSession();
        var shopOrder = await session.LoadAsync<ShopOrder>(ShopDocumentIds.Order(request.OrderId, request.OrganizationId), cancellationToken)
            ?? throw ShopException.Of(ShopRefusals.OrderNotFound);
        var ret = shopOrder.Returns.FirstOrDefault(r => r.ReturnId == request.ReturnId) ?? throw ShopException.Of(ShopRefusals.ReturnNotFound, request.ReturnId);
        var deductions = (request.Deductions ?? []).Where(d => ret.Lines.Any(l => l.LineId == d.LineId)).ToList();
        if (deductions.Any(d => string.IsNullOrWhiteSpace(d.Reason)))
            throw ShopException.Of(ShopRefusals.ReasonRequired);

        var now = clock.GetUtcNow();
        // The receipt (once), then the disposition: Checkout restocks a Restock disposition exactly once (I4), on the
        // first registration or on a later one that changes the disposition to Restock.
        await refunds.RegisterGoodsBackAsync(new GoodsBackRegistration(
            shopOrder.OrderId, ret.ReturnId, GoodsBackKind.Received, ret.ReceivedAt ?? ret.ArrivedAt ?? now, disposition,
            ret.ReceivedAt is null ? deductions.Select(d => new DiminishedValue(d.LineId, d.Amount, d.Reason.Trim())).ToList() : null,
            access.UserId), cancellationToken);
        ret.ReceivedAt ??= ret.ArrivedAt ?? now;
        ret.Disposition = disposition;
        ret.InspectedAt = now;
        ret.InspectedBy = access.UserId;
        ret.InspectionNote = request.Note?.Trim() is { Length: > 0 } note ? note[..Math.Min(note.Length, 2000)] : null;
        shopOrder.UpdatedAt = now;
        await session.SaveChangesAsync(cancellationToken);
    }

    // ---- stock (Warehouse) -----------------------------------------------------------------------------------------

    public async Task<bool> SetStockAsync(string variantId, int capacity, CancellationToken cancellationToken = default)
    {
        var quotaId = ShopQuotas.Variant(variantId);
        var target = Math.Max(0, capacity);
        await inventory.CreateQuotaAsync(quotaId, target, ShopQuotas.Shards(target, options.CurrentValue.ShardsPerQuota), cancellationToken);
        var result = await inventory.SetCapacityAsync(quotaId, target, cancellationToken);
        return result.Succeeded;
    }
}
