using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Fulfilment.Models;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Shop;
using MintPlayer.Spark.Commerce.Shop.Catalog;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Commerce.Shop.Orders;
using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Replication.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Commerce.Fulfilment.Services;

/// <summary>
/// The shop side of one shop order at the warehouse (warehouse §4.3, WD2), re-derived from state on every call: Checkout's
/// order and refunds, the shop order, and the warehouse's replicas (status, shipments, return receipts). One owner per fact:
/// the shop writes only its own documents (fulfilment orders, expected returns, its bookkeeping) and tells Shop and Checkout
/// what the warehouse reported; it never writes a warehouse document. Every step is idempotent and order-insensitive, so the
/// order events, the replica notifications and the reconcile all call the same method.
/// </summary>
/// <remarks>
/// The calls into Shop, Checkout and the inventory run before the bookkeeping is saved and are idempotent (per shipment, per
/// return, per inventory return id), so a crash or a concurrent run repeats them harmlessly. The published documents and
/// their pushes commit in one session (<see cref="IReplicaPublisher"/>).
/// </remarks>
internal sealed class FulfilmentSync(
    IDocumentStore store,
    ISparkShopOrders shopOrders,
    ISparkCheckoutRefunds refunds,
    ISparkCheckoutUndelivered undelivered,
    ISparkInventory inventory,
    IReplicaPublisher publisher,
    FulfilmentCatalog catalog,
    IOptionsMonitor<SparkCommerceFulfilmentOptions> options,
    TimeProvider clock,
    ILogger<FulfilmentSync> logger)
{
    private const int MaxAttempts = 3;

    /// <summary>The note Checkout stores on an undelivered-goods refund (<c>RefundService.RefundUndeliveredAsync</c>).</summary>
    internal const string UndeliveredNote = "undelivered ";

    // Shop and Checkout raise order events while this runs (LineShipped, RefundIssued); their recipient would sync the same
    // order again inside this run. The outer run finishes it; a later event or the reconcile picks up anything newer.
    private static readonly AsyncLocal<HashSet<string>?> Running = new();

    private SparkCommerceFulfilmentOptions Options => options.CurrentValue;

    /// <summary>Syncs every seller's part of <paramref name="orderId"/> that the warehouse ships.</summary>
    public async Task SyncOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        CheckoutOrder? order;
        using (var session = store.OpenAsyncSession())
            order = await session.LoadAsync<CheckoutOrder>(orderId, cancellationToken);
        if (order is null)
            return;
        foreach (var seller in order.Lines.Where(l => FulfilmentContractBuilder.IsWarehouseLine(l, Options)).Select(l => l.SellerOrganizationId).Distinct())
            await SyncAsync(orderId, seller, cancellationToken);
    }

    /// <summary>Syncs the shop order a link (or a fulfilment order, expected return) belongs to.</summary>
    public async Task SyncLinkAsync(string linkId, CancellationToken cancellationToken)
    {
        FulfilmentLink? link;
        using (var session = store.OpenAsyncSession())
            link = await session.LoadAsync<FulfilmentLink>(linkId, cancellationToken);
        if (link is not null)
            await SyncAsync(link.OrderId, link.SellerOrganizationId, cancellationToken);
    }

    public async Task SyncAsync(string orderId, string? sellerOrganizationId, CancellationToken cancellationToken)
    {
        var key = orderId + "\u001f" + sellerOrganizationId;
        var running = Running.Value ??= new HashSet<string>(StringComparer.Ordinal);
        if (!running.Add(key))
            return;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await SyncOnceAsync(orderId, sellerOrganizationId, cancellationToken);
                    return;
                }
                catch (ConcurrencyException) when (attempt < MaxAttempts)
                {
                    logger.LogDebug("Fulfilment of order {OrderId} changed meanwhile; deriving it again.", orderId);
                }
            }
        }
        finally
        {
            running.Remove(key);
        }
    }

    /// <summary>The states in which a confirmed order's goods are handed to the warehouse.</summary>
    internal static bool Publishable(OrderState state)
        => state is OrderState.Confirmed or OrderState.PartiallyRefunded or OrderState.Disputed or OrderState.DisputeWon;

    private async Task SyncOnceAsync(string orderId, string? seller, CancellationToken cancellationToken)
    {
        var o = Options;
        var now = clock.GetUtcNow();
        using var session = store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;

        var order = await session.LoadAsync<CheckoutOrder>(orderId, cancellationToken);
        if (order is null)
            return;
        var shopOrder = await session.LoadAsync<ShopOrder>(ShopDocumentIds.Order(orderId, seller), cancellationToken);
        if (shopOrder is null || shopOrder.Fulfilment != ShopFulfilmentMode.Warehouse)
            return;

        var linkId = FulfilmentIds.Link(o.ShopName, order.Number, seller);
        var link = await session.LoadAsync<FulfilmentLink>(linkId, cancellationToken);
        if (link is null)
        {
            if (!Publishable(order.State))
                return;
            var first = order.Lines.FirstOrDefault(l => l.SellerOrganizationId == seller && FulfilmentContractBuilder.IsWarehouseLine(l, o));
            if (first is null)
                return;
            link = new FulfilmentLink
            {
                Id = linkId, ShopOrderId = shopOrder.Id!, OrderId = orderId, OrderNumber = order.Number, SellerOrganizationId = seller,
                OwnerId = o.OwnerOf(seller), Channel = first.Channel, CreatedAt = now, UpdatedAt = now,
            };
            await session.StoreAsync(link, linkId, cancellationToken);
            // The warehouse refuses an unknown SKU at intake: the order's items go out first (their own commit).
            await catalog.PublishAsync(shopOrder.Lines.Select(l => l.VariantId), cancellationToken);
        }

        var ledger = await session.LoadAsync<CheckoutRefundLedger>(CheckoutRefundLedger.IdFor(orderId), cancellationToken);
        var views = await ViewsAsync(session, link, cancellationToken);

        // 1. What left the warehouse, to Shop (and through it Checkout), before planning: a refund after a shipment is a return.
        if (await ReportShipmentsAsync(link, shopOrder, views, now, cancellationToken))
            await session.Advanced.RefreshAsync(order, cancellationToken);

        // 2. What the buyer should still receive against what the warehouse shipped, may ship and cancelled.
        var lines = PlannedLines(order, ledger, shopOrder, seller, o);
        var address = link.ShipToOverride ?? FulfilmentContractBuilder.Address(shopOrder);
        var plan = FulfilmentPlanner.Plan(lines, views, address is null ? null : FulfilmentPlanner.AddressKey(address), now);

        // Units the warehouse cancelled on its own are refunded at once (lead decision 2026-10-20; CRD Art. 18, Art. 13 for
        // the delivery cost, both unconfirmed, D37). The refund keeps them sold in Checkout (a stock-feed line); they are
        // restocked below like any unshipped refund, once nothing can ship them (§4.4 rule 1). Only a refused refund is
        // left to staff (the attention flag).
        if (plan.Lines.Any(l => l.Value.Undeliverable > 0)
            && await RefundUndeliverableAsync(link, plan, views, cancellationToken))
        {
            await session.Advanced.RefreshAsync(order, cancellationToken);
            if (ledger is null)
                ledger = await session.LoadAsync<CheckoutRefundLedger>(CheckoutRefundLedger.IdFor(orderId), cancellationToken);
            else
                await session.Advanced.RefreshAsync(ledger, cancellationToken);
            lines = PlannedLines(order, ledger, shopOrder, seller, o);
            plan = FulfilmentPlanner.Plan(lines, views, address is null ? null : FulfilmentPlanner.AddressKey(address), now);
        }
        var holds =order.State == OrderState.Disputed ? new[] { FulfilmentContractBuilder.DisputeHold } : [];

        foreach (var view in views.Where(v => plan.CancelRevisionIds.Contains(v.Revision.Id)))
        {
            // Cancellation is a status on the shop's own order (WD2 handshake); the warehouse answers in its status.
            view.Revision.CancellationRequestedAt = now;
            if (view.Published is { } published)
            {
                published.CancellationRequested = true;
                published.CancellationLineIds = [];
                await publisher.MarkChangedAsync(session, typeof(FulfilmentOrder), published.Id!, cancellationToken);
            }
        }

        if (plan.NewRevision.Count > 0 && address is not null)
        {
            var number = link.Revisions.Count == 0 ? 1 : link.Revisions.Max(r => r.Number) + 1;
            var id = FulfilmentIds.Order(o.ShopName, order.Number, seller, number);
            var document = FulfilmentContractBuilder.Order(id, link, order, shopOrder, plan.NewRevision, address, holds, o);
            await session.StoreAsync(document, id, cancellationToken);
            await publisher.MarkChangedAsync(session, typeof(FulfilmentOrder), id, cancellationToken);
            link.Revisions.Add(new FulfilmentRevision { Id = id, Number = number, Lines = [.. plan.NewRevision], PublishedAt = now });
        }

        // Hold codes follow the order (an open chargeback holds what is not picked yet).
        foreach (var view in views.Where(v => v.Published is not null && v.Revision.CancellationRequestedAt is null && lines.Any(l => v.Outstanding(l.LineId) > 0)))
            if (!view.Published!.Holds.SequenceEqual(holds))
            {
                view.Published.Holds = [.. holds];
                await publisher.MarkChangedAsync(session, typeof(FulfilmentOrder), view.Published.Id!, cancellationToken);
            }

        // 3. §4.4 rule 1: refunded units go back on sale once they can no longer ship.
        await RestockAsync(order, ledger, plan, cancellationToken);

        // 4. Returns: expected returns with the reason per line (GA8), the refund timing (WD12, GA1), the warehouse's receipts.
        if (ledger is not null)
            await PublishReturnsAsync(session, link, order, ledger, shopOrder, views, now, cancellationToken);
        await ReportReceiptsAsync(session, link, cancellationToken);

        link.Attention = plan.Attention;
        // An automatic refund of undeliverable units the provider refused stays with staff (retry or settle by hand).
        if (link.Attention is null && ledger?.Refunds.FirstOrDefault(r => r.Note?.StartsWith(UndeliveredNote, StringComparison.Ordinal) == true
                && r.Status is RefundStatus.Failed or RefundStatus.ManualRequired) is { } failed)
            link.Attention = new FulfilmentAttention { Kind = FulfilmentAttentionKind.Undeliverable, Detail = $"refund {failed.RefundId}: {failed.Status} {failed.FailureReason}".Trim(), At = now };
        var returnsOpen = link.Returns.Any(r => r.ReceivedAt is null && r.PublishedAt + o.DeliveryWatch > now);
        var deliveriesOpen = link.ShipmentsReported.Except(link.DeliveriesReported, StringComparer.Ordinal).Any() && (link.LastShippedAt ?? now) + o.DeliveryWatch > now;
        link.Closed = plan.Settled && plan.Attention is null && !returnsOpen && !deliveriesOpen;
        link.CheckedAt = now;
        link.UpdatedAt = now;
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task<List<RevisionView>> ViewsAsync(IAsyncDocumentSession session, FulfilmentLink link, CancellationToken cancellationToken)
    {
        var views = new List<RevisionView>(link.Revisions.Count);
        foreach (var revision in link.Revisions)
        {
            var published = await session.LoadAsync<FulfilmentOrder>(revision.Id, cancellationToken);
            var status = await session.LoadAsync<FulfilmentOrderStatus>(FulfilmentIds.Status(revision.Id), cancellationToken);
            var shipments = (await session.Advanced.LoadStartingWithAsync<FulfilmentShipment>(FulfilmentIds.ShipmentsPrefix(revision.Id), pageSize: 128, token: cancellationToken))
                .Where(s => s.FulfilmentOrderId == revision.Id).ToList();
            views.Add(new RevisionView(revision, published, status, shipments));
        }
        return views;
    }

    /// <summary>The goods lines of this shop order as Checkout has them now. Pure.</summary>
    internal static List<PlannedLine> PlannedLines(CheckoutOrder order, CheckoutRefundLedger? ledger, ShopOrder shopOrder, string? seller, SparkCommerceFulfilmentOptions options)
        => order.Lines
            .Where(l => l.SellerOrganizationId == seller && FulfilmentContractBuilder.IsWarehouseLine(l, options) && shopOrder.Lines.Any(x => x.LineId == l.LineId))
            .Select(l => new PlannedLine(
                l.LineId,
                l.Quantity,
                ledger is null ? 0 : UnshippedRefunds(ledger, l.LineId).Sum(x => x.Units.Quantity),
                Gone: l.Status == OrderLineStatus.Refused || (l.Status == OrderLineStatus.Returned && l.ShippedAt is null)))
            .ToList();

    /// <summary>
    /// Refund units of a line that were never shipped: a refund without a return (a cancellation, a refused line), or a
    /// return line Checkout recorded as not shipped (an early withdrawal). Goods coming back are a return, not this.
    /// </summary>
    internal static IEnumerable<(CheckoutRefund Refund, RefundUnits Units)> UnshippedRefunds(CheckoutRefundLedger ledger, string lineId)
        => ledger.Refunds.SelectMany(r => r.Lines.Where(u => u.LineId == lineId && u.Quantity > 0).Select(u => (Refund: r, Units: u)))
            .Where(x => x.Refund.ReturnId is null
                || ledger.Returns.FirstOrDefault(ret => ret.ReturnId == x.Refund.ReturnId)?.Lines.FirstOrDefault(l => l.LineId == lineId) is not { Shipped: true });

    /// <summary>Checkout's inventory return id of a refund's line (RefundEngine.InventoryReturnId: <c>{orderId}:{refundId}:{lineId}</c>).</summary>
    internal static string InventoryReturnId(string orderId, string refundId, string lineId) => orderId + ":" + refundId + ":" + lineId;

    private async Task<bool> ReportShipmentsAsync(FulfilmentLink link, ShopOrder shopOrder, IReadOnlyList<RevisionView> views, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reported = false;
        foreach (var shipment in views.SelectMany(v => v.Shipments).Where(s => s.Id is not null && s.ShippedAt is not null).OrderBy(s => s.ShippedAt))
        {
            if (!link.ShipmentsReported.Contains(shipment.Id!))
            {
                var units = shipment.Lines.Where(l => shopOrder.Lines.Any(x => x.LineId == l.LineId))
                    .GroupBy(l => l.LineId, StringComparer.Ordinal)
                    .Select(g => new ShopShippedLine { LineId = g.Key, Quantity = RevisionView.Units(g.Sum(l => l.Quantity)) })
                    .Where(u => u.Quantity > 0).ToList();
                if (units.Count > 0)
                {
                    await shopOrders.MarkShippedAsync(link.OrderId, link.SellerOrganizationId, units, shipment.ShippedAt!.Value, shipment.Id!, cancellationToken);
                    reported = true;
                }
                link.ShipmentsReported.Add(shipment.Id!);
                if (link.LastShippedAt is null || shipment.ShippedAt > link.LastShippedAt)
                    link.LastShippedAt = shipment.ShippedAt;
            }
            if (shipment.TrackingState == FulfilmentTrackingState.Delivered && !link.DeliveriesReported.Contains(shipment.Id!))
            {
                // The contract carries no delivery instant: the moment the shop learns it, which is never before the
                // delivery (so the withdrawal and guarantee periods, CRD Art. 9(2)(b), never start early).
                await shopOrders.MarkDeliveredAsync(link.OrderId, link.SellerOrganizationId, shipment.Id!, now, cancellationToken);
                link.DeliveriesReported.Add(shipment.Id!);
            }
        }
        return reported;
    }

    /// <summary>
    /// Refunds every line's undeliverable units (cancelled by the warehouse, not refunded yet). The reference is the line's
    /// cumulative cancelled count, so a repeat refunds nothing and a later cancellation only the new units. False when
    /// nothing was refunded (or Checkout refused, which keeps the attention flag for staff).
    /// </summary>
    private async Task<bool> RefundUndeliverableAsync(FulfilmentLink link, FulfilmentPlan plan, IReadOnlyList<RevisionView> views, CancellationToken cancellationToken)
    {
        var lines = plan.Lines.Where(l => l.Value.Undeliverable > 0)
            .Select(l => (LineId: l.Key, l.Value.Undeliverable, Cancelled: views.Sum(v => v.Undeliverable(l.Key)))).ToList();
        var reference = $"{link.Id}:" + string.Join(",", lines.Select(l => $"{l.LineId}={l.Cancelled}"));
        try
        {
            var refundId = await undelivered.RefundUndeliveredAsync(
                new UndeliveredRefund(link.OrderId, reference, lines.Select(l => new UndeliveredLine(l.LineId, l.Undeliverable)).ToList()), cancellationToken);
            return refundId is not null;
        }
        catch (CheckoutException ex)
        {
            logger.LogWarning(ex, "Checkout refused to refund the units the warehouse cancelled for order {OrderId}; staff refund them.", link.OrderId);
            return false;
        }
    }

    private async Task RestockAsync(CheckoutOrder order, CheckoutRefundLedger? ledger, FulfilmentPlan plan, CancellationToken cancellationToken)
    {
        foreach (var line in order.Lines)
        {
            if (!plan.Lines.TryGetValue(line.LineId, out var figures) || figures.Restockable <= 0 || line.ClaimId is null || line.Capacity.IsEmpty)
                continue;
            var claim = await inventory.GetClaimAsync(line.ClaimId, cancellationToken);
            if (claim is null)
                continue;
            var ids = ledger is null ? [] : UnshippedRefunds(ledger, line.LineId).Select(x => InventoryReturnId(order.Id!, x.Refund.RefundId, line.LineId)).ToHashSet(StringComparer.Ordinal);
            var records = claim.Returns
                .Where(r => ids.Contains(r.Id) || (r.Reason == ReturnReason.DisputeLost && r.Id.EndsWith(":" + line.LineId, StringComparison.Ordinal)))
                .OrderBy(r => r.At).ToList();
            var remaining = figures.Restockable - records.Where(r => r.StockReturned).Sum(UnitsOf);
            foreach (var record in records.Where(r => !r.StockReturned))
            {
                var units = UnitsOf(record);
                if (units > remaining)
                    break;
                var result = await inventory.RestockAsync(line.ClaimId, record.Id, cancellationToken);
                if (!result.Succeeded)
                {
                    logger.LogWarning("Restocking return {ReturnId} of line {LineId} (order {OrderId}) answered {Status}.", record.Id, line.LineId, order.Id, result.Status);
                    break;
                }
                remaining -= units;
            }
        }
    }

    /// <summary>The units of a claim return: its largest quota allocation (a shop line counts against one quota).</summary>
    private static int UnitsOf(ClaimReturn record)
        => record.Allocations.Count == 0 ? 0 : record.Allocations.GroupBy(a => a.QuotaId, StringComparer.Ordinal).Max(g => g.Sum(a => a.N));

    private async Task PublishReturnsAsync(IAsyncDocumentSession session, FulfilmentLink link, CheckoutOrder order, CheckoutRefundLedger ledger, ShopOrder shopOrder,
        IReadOnlyList<RevisionView> views, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var o = Options;
        foreach (var ret in ledger.Returns)
        {
            if (link.Returns.Any(r => r.ReturnId == ret.ReturnId))
                continue;
            var back = ret.Lines.Where(l => l.Eligible && l.Shipped && l.Quantity > 0 && shopOrder.Lines.Any(x => x.LineId == l.LineId)).ToList();
            if (back.Count == 0)
                continue;
            // One expected return per fulfilment order the goods left in (the contract names one order).
            var byRevision = back
                .Select(l => (Line: l, View: views.Where(v => v.Shipped(l.LineId) > 0).OrderByDescending(v => v.Shipped(l.LineId)).FirstOrDefault()
                    ?? views.LastOrDefault(v => v.Asked(l.LineId) > 0)))
                .Where(x => x.View is not null)
                .GroupBy(x => x.View!.Revision.Number);
            var published = new FulfilmentLinkReturn { ReturnId = ret.ReturnId, PublishedAt = now };
            foreach (var group in byRevision)
            {
                var revision = group.First().View!.Revision;
                var id = FulfilmentIds.ExpectedReturn(o.ShopName, link.OrderNumber, link.SellerOrganizationId, ret.ReturnId, revision.Number);
                await session.StoreAsync(FulfilmentContractBuilder.ExpectedReturn(id, link, revision.Id, ret, shopOrder, group.Select(x => x.Line).ToList()), id, cancellationToken);
                await publisher.MarkChangedAsync(session, typeof(FulfilmentExpectedReturn), id, cancellationToken);
                published.ExpectedReturnIds.Add(id);
            }
            if (published.ExpectedReturnIds.Count > 0)
                link.Returns.Add(published);
        }

        if (o.RefundTiming != FulfilmentRefundTiming.OnRequest)
            return;
        // WD12 "refund on request": the withheld refund is paid at once; the receipt still records the disposition later.
        foreach (var published in link.Returns.Where(r => r.ReleasedAt is null && r.ReceivedAt is null))
        {
            if (ledger.Returns.FirstOrDefault(r => r.ReturnId == published.ReturnId) is not { Status: ReturnStatus.AwaitingGoods })
                continue;
            await refunds.RegisterGoodsBackAsync(new GoodsBackRegistration(order.Id!, published.ReturnId, GoodsBackKind.Released, now), cancellationToken);
            published.ReleasedAt = now;
        }
    }

    private async Task ReportReceiptsAsync(IAsyncDocumentSession session, FulfilmentLink link, CancellationToken cancellationToken)
    {
        var expected = link.Returns.SelectMany(r => r.ExpectedReturnIds).ToList();
        if (expected.Count == 0)
            return;
        // LINQ: the ids are query parameters, never spliced into RQL.
        var receipts = await session.Query<FulfilmentReturnReceipt>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(r => r.ExpectedReturnId!.In(expected))
            .Take(256)
            .ToListAsync(cancellationToken);
        foreach (var receipt in receipts.Where(r => r.Id is not null && !link.ReceiptsReported.Contains(r.Id)).OrderBy(r => r.ReceivedAt))
        {
            var published = link.Returns.First(r => r.ExpectedReturnIds.Contains(receipt.ExpectedReturnId!));
            try
            {
                // The warehouse's receipt: a refund withheld under CRD Art. 13(3) is paid now unless the buyer's proof of
                // dispatch already released it (whichever is earlier, GA1); the disposition is recorded (Checkout restocks
                // nothing for a warehouse line: the warehouse's restock raised its on-hand, §4.4 rule 4).
                await refunds.RegisterGoodsBackAsync(new GoodsBackRegistration(link.OrderId, published.ReturnId, GoodsBackKind.Received, receipt.ReceivedAt,
                    FulfilmentContractBuilder.Disposition(receipt.Lines)), cancellationToken);
            }
            catch (CheckoutException ex)
            {
                logger.LogWarning(ex, "Checkout refused the warehouse's receipt {ReceiptId} of return {ReturnId} (order {OrderId}).", receipt.Id, published.ReturnId, link.OrderId);
            }
            link.ReceiptsReported.Add(receipt.Id!);
            published.ReceivedAt ??= receipt.ReceivedAt;
        }
    }
}
