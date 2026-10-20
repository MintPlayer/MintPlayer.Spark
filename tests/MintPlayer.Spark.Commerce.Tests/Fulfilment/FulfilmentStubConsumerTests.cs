using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Fulfilment;
using MintPlayer.Spark.Commerce.Fulfilment.Models;
using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Testing;
using static MintPlayer.Spark.Commerce.Tests.Events.EventFixtures;

namespace MintPlayer.Spark.Commerce.Tests.Fulfilment;

/// <summary>Records the mails a test sends.</summary>
internal sealed class RecordingMailer : ISparkMailer
{
    public List<SparkMailRequest> Sent { get; } = [];

    public Task<SparkMailReceipt> SendAsync(SparkMailRequest request, CancellationToken cancellationToken = default)
    {
        Sent.Add(request);
        return Task.FromResult<SparkMailReceipt>(null!);
    }

    public Task SendCampaignAsync(SparkMailCampaign campaign, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>
/// The full shop ↔ warehouse cycle through the contracts alone (W8, commerce S20): a stub warehouse that knows nothing but
/// <c>MintPlayer.Spark.Fulfilment.Contracts</c> reads the shop's orders, catalog items and expected returns and answers
/// with its own status, shipments, stock levels and return receipts; the shop derives shipping, delivery, refunds on
/// receipt, stock and recall notices from them. Replication itself (the push, the change notifications) is tested in
/// the replication suite; here its documents are simply present, as after a delivery.
/// </summary>
public class FulfilmentStubConsumerTests : SparkTestDriver
{
    [Fact]
    public void The_contracts_reference_no_commerce_package_W8()
    {
        var references = typeof(FulfilmentOrder).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        references.Should().NotContain(name => name.StartsWith("MintPlayer.Spark.Commerce", StringComparison.Ordinal));
        references.Should().NotContain(name => name.StartsWith("MintPlayer.Spark.Warehouse", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_confirmed_order_is_published_with_its_catalog_items_and_answered_through_the_warehouses_own_documents()
    {
        var host = new FulfilmentHost(Store);
        await host.SeedAsync();
        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);

        // Published: one fulfilment order, its id prefixed by the shop (unique across shops), lines by Checkout line id.
        var order = await host.Warehouse.OrderAsync(FulfilmentHost.Revision(1));
        order.Should().NotBeNull();
        order!.OwnerId.Should().Be(FulfilmentHost.ShopName);
        order.Channel.Should().Be("shop");
        order.OrderNumber.Should().Be(FulfilmentHost.Number);
        order.Lines.Select(l => (l.LineId, l.Sku, l.Quantity)).Should().Equal(("L1", "OIL-75", 2m), ("L2", "OIL-50", 1m));
        order.ShipTo.Line1.Should().Be("Kerkstraat 1");
        order.ShipTo.CountryCode.Should().Be("BE");
        order.CancellationRequested.Should().BeFalse();
        var item = await host.Warehouse.CatalogItemAsync($"FulfilmentCatalogItems/{FulfilmentHost.ShopName}/{FulfilmentHost.ShopName}/OIL-75");
        item.Should().NotBeNull();
        item!.Gtins.Should().Equal("5400000000005");
        item.Variant.Should().Be("size: 75 cl");
        host.Publisher.Changed.Should().Contain((typeof(FulfilmentOrder), FulfilmentHost.Revision(1)));
        host.Publisher.Changed.Count(c => c.Type == typeof(FulfilmentCatalogItem)).Should().Be(2);

        // A redelivered event publishes nothing new.
        var pushes = host.Publisher.Changed.Count;
        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        host.Publisher.Changed.Count.Should().Be(pushes);

        // The warehouse accepts, ships L1 in two parcels' worth and L2, then the carrier delivers.
        await host.Warehouse.AnswerAsync(order, FulfilmentOrderState.Accepted);
        var first = await host.Warehouse.ShipAsync(order, 1, "LOT-7", FulfilmentTrackingState.InTransit, ("L1", 2));
        var second = await host.Warehouse.ShipAsync(order, 2, "LOT-8", FulfilmentTrackingState.Delivered, ("L2", 1));
        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        host.Shop.Shipped.Select(s => s.Reference).Should().Equal(first.Id!, second.Id!);
        host.Shop.Shipped[0].Lines.Single().Quantity.Should().Be(2);
        host.Shop.Shipped[0].At.Should().Be(first.ShippedAt!.Value);
        host.Shop.Delivered.Select(d => d.Reference).Should().Equal(second.Id!);

        // Reported once each, whatever the number of notifications.
        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        host.Shop.Shipped.Should().HaveCount(2);
        host.Shop.Delivered.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_refund_before_shipment_is_a_cancellation_handshake_and_sold_drops_only_on_the_confirmation()
    {
        var host = new FulfilmentHost(Store);
        await host.SeedAsync();
        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        var revision1 = (await host.Warehouse.OrderAsync(FulfilmentHost.Revision(1)))!;
        await host.Warehouse.AnswerAsync(revision1, FulfilmentOrderState.Accepted);

        // The buyer cancels one unit of L1 (no return: nothing shipped). Checkout kept the unit sold (ICheckoutStockFeed).
        var returnId = $"{FulfilmentHost.OrderId}:r-1:L1";
        await host.StoreAsync(new CheckoutRefundLedger
        {
            OrderId = FulfilmentHost.OrderId,
            Refunds = [new CheckoutRefund { RefundId = "r-1", Kind = RefundKind.Cancellation, Lines = [new RefundUnits { LineId = "L1", Quantity = 1, StockReturned = false }] }],
        }, CheckoutRefundLedger.IdFor(FulfilmentHost.OrderId));
        host.Claim("claims/L1", FulfilmentHost.VariantA, (returnId, 1, false));

        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        (await host.Warehouse.OrderAsync(FulfilmentHost.Revision(1)))!.CancellationRequested.Should().BeTrue();
        host.Inventory.Restocked.Should().BeEmpty(); // rule 1: never on the request
        (await host.Warehouse.OrderAsync(FulfilmentHost.Revision(2))).Should().BeNull(); // nothing new until the answer

        // The warehouse accepts: the unit goes back on sale, the rest is ordered again (edit = cancellation + new order).
        await host.Warehouse.AnswerAsync(revision1, FulfilmentOrderState.Cancelled, s =>
        {
            s.Cancellation = FulfilmentCancellationAnswer.Accepted;
            foreach (var line in s.Lines)
                line.Cancelled = revision1.Lines.First(l => l.LineId == line.LineId).Quantity;
        });
        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        host.Inventory.Restocked.Should().Equal(("claims/L1", returnId));
        var revision2 = await host.Warehouse.OrderAsync(FulfilmentHost.Revision(2));
        revision2.Should().NotBeNull();
        revision2!.Lines.Select(l => (l.LineId, l.Quantity)).Should().Equal(("L1", 1m), ("L2", 1m));

        // Exactly once (I4), however often it is derived again.
        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        host.Inventory.Restocked.Should().HaveCount(1);
        (await host.Warehouse.OrderAsync(FulfilmentHost.Revision(3))).Should().BeNull();
    }

    [Fact]
    public async Task Units_the_warehouse_cancels_on_its_own_are_refunded_automatically_then_put_back_on_sale()
    {
        var host = new FulfilmentHost(Store);
        await host.SeedAsync();
        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        var order = (await host.Warehouse.OrderAsync(FulfilmentHost.Revision(1)))!;

        // CancelRemainder: one of L1's two units shipped, the other cancelled; nobody asked (lead decision 2026-10-20).
        await host.Warehouse.ShipAsync(order, 1, "LOT-7", FulfilmentTrackingState.InTransit, ("L1", 1), ("L2", 1));
        await host.Warehouse.AnswerAsync(order, FulfilmentOrderState.Shipped, s =>
        {
            s.Lines.First(l => l.LineId == "L1").Shipped = 1;
            s.Lines.First(l => l.LineId == "L1").Cancelled = 1;
            s.Lines.First(l => l.LineId == "L2").Shipped = 1;
        });
        host.Claim("claims/L1", FulfilmentHost.VariantA, ($"{FulfilmentHost.OrderId}:u-1:L1", 1, false));

        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        var request = host.Undelivered.Requests.Single();
        request.OrderId.Should().Be(FulfilmentHost.OrderId);
        request.Lines.Select(l => (l.LineId, l.Quantity)).Should().Equal(("L1", 1));
        // Nothing can ship it any more: back on sale through the feed, never ordered again, no staff attention.
        host.Inventory.Restocked.Should().Equal(("claims/L1", $"{FulfilmentHost.OrderId}:u-1:L1"));
        (await host.Warehouse.OrderAsync(FulfilmentHost.Revision(2))).Should().BeNull();
        (await host.LoadAsync<FulfilmentLink>(FulfilmentIds.Link(FulfilmentHost.ShopName, FulfilmentHost.Number, null)))!.Attention.Should().BeNull();

        // Once refunded, a repeat asks nothing again.
        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        host.Undelivered.Requests.Should().HaveCount(1);
        host.Inventory.Restocked.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_withdrawal_reaches_the_warehouse_with_its_reason_and_its_receipt_pays_the_withheld_refund()
    {
        var host = new FulfilmentHost(Store);
        await host.SeedAsync();
        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        var order = (await host.Warehouse.OrderAsync(FulfilmentHost.Revision(1)))!;
        await host.Warehouse.AnswerAsync(order, FulfilmentOrderState.Shipped, s => { foreach (var line in s.Lines) line.Shipped = order.Lines.First(l => l.LineId == line.LineId).Quantity; });
        await host.Warehouse.ShipAsync(order, 1, "LOT-7", FulfilmentTrackingState.Delivered, ("L1", 2), ("L2", 1));
        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        await host.UpdateAsync<CheckoutOrder>(FulfilmentHost.OrderId, o => { foreach (var line in o.Lines) line.ShippedAt = Now; });

        // GA1: the buyer withdraws from L1; Checkout withholds the refund until the goods are back (CRD Art. 13(3)).
        await host.StoreAsync(new CheckoutRefundLedger
        {
            OrderId = FulfilmentHost.OrderId,
            Returns =
            [
                new CheckoutReturn
                {
                    ReturnId = "w-1", Reason = ReturnReasonCode.Withdrawal, Status = ReturnStatus.AwaitingGoods, ReceivedAt = Now.AddDays(3),
                    Lines = [new CheckoutReturnLine { LineId = "L1", Quantity = 2, Eligible = true, Shipped = true }],
                },
            ],
        }, CheckoutRefundLedger.IdFor(FulfilmentHost.OrderId));
        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);

        var expectedId = $"FulfilmentExpectedReturns/{FulfilmentHost.ShopName}/{FulfilmentHost.Number}/platform/w-1/1";
        var expected = await host.Warehouse.ExpectedReturnAsync(expectedId);
        expected.Should().NotBeNull();
        expected!.FulfilmentOrderId.Should().Be(FulfilmentHost.Revision(1));
        expected.Lines.Single().Reason.Should().Be(FulfilmentReturnReason.Withdrawal); // GA8: the reason per line
        expected.Lines.Single().Sku.Should().Be("OIL-75");
        host.Refunds.GoodsBack.Should().BeEmpty(); // on receipt by default (WD12)

        // The warehouse receives and restocks: Checkout pays the withheld refund; it restocks nothing itself (rule 4).
        await host.Warehouse.ReceiveAsync(expected, FulfilmentReturnDisposition.Restock);
        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        host.Refunds.GoodsBack.Should().ContainSingle();
        host.Refunds.GoodsBack[0].ReturnId.Should().Be("w-1");
        host.Refunds.GoodsBack[0].Kind.Should().Be(GoodsBackKind.Received);
        host.Refunds.GoodsBack[0].Disposition.Should().Be(ReturnDisposition.Restock);
        host.Inventory.Restocked.Should().BeEmpty();

        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        host.Refunds.GoodsBack.Should().ContainSingle(); // once
    }

    [Fact]
    public async Task With_refund_on_request_the_withheld_refund_is_released_when_the_return_is_published_WD12()
    {
        var host = new FulfilmentHost(Store, o => o.RefundTiming = FulfilmentRefundTiming.OnRequest);
        await host.SeedAsync();
        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        var order = (await host.Warehouse.OrderAsync(FulfilmentHost.Revision(1)))!;
        await host.Warehouse.ShipAsync(order, 1, "LOT-7", FulfilmentTrackingState.Delivered, ("L1", 2));
        await host.StoreAsync(new CheckoutRefundLedger
        {
            OrderId = FulfilmentHost.OrderId,
            Returns = [new CheckoutReturn { ReturnId = "c-1", Reason = ReturnReasonCode.NonConformity, Status = ReturnStatus.AwaitingGoods, Lines = [new CheckoutReturnLine { LineId = "L1", Quantity = 1, Eligible = true, Shipped = true }] }],
        }, CheckoutRefundLedger.IdFor(FulfilmentHost.OrderId));

        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        host.Refunds.GoodsBack.Should().ContainSingle();
        host.Refunds.GoodsBack[0].Kind.Should().Be(GoodsBackKind.Released);
        var expected = await host.Warehouse.ExpectedReturnAsync($"FulfilmentExpectedReturns/{FulfilmentHost.ShopName}/{FulfilmentHost.Number}/platform/c-1/1");
        expected!.Lines.Single().Reason.Should().Be(FulfilmentReturnReason.NonConformity);
    }

    [Fact]
    public async Task Stock_levels_set_the_quota_ignore_older_snapshots_and_add_the_onboarding_baseline()
    {
        var host = new FulfilmentHost(Store);
        await host.SeedAsync();
        await host.Catalog.PublishAsync([FulfilmentHost.VariantA], default);

        var level = await host.Warehouse.StockAsync(FulfilmentHost.ShopName, "OIL-75", 10, new FulfilmentChannelStock { Channel = "shop", ShippedTotal = 4 });
        (await host.Stock.ApplyAsync(level, force: false, default)).Should().BeTrue();
        host.Shop.Stock.Should().Equal((FulfilmentHost.VariantA, 14));

        // The same or an older version changes nothing (§4.4: superseded by Version).
        (await host.Stock.ApplyAsync(level, force: false, default)).Should().BeFalse();
        (await host.Stock.ApplyAsync(new FulfilmentStockLevel { OwnerId = level.OwnerId, Sku = level.Sku, OnHandSellable = 99, Version = level.Version - 1 }, false, default)).Should().BeFalse();
        host.Shop.Stock.Should().HaveCount(1);

        // Rule 2: units the shop shipped itself before the warehouse took over.
        await host.Stock.SetBaselineAsync(FulfilmentHost.VariantA, 6, default);
        host.Shop.Stock[^1].Should().Be((FulfilmentHost.VariantA, 20));

        // Shrinkage below what is sold and held: the quota closes there (the shop stops selling).
        host.Shop.Floor = 12;
        host.Inventory.Availability = new MintPlayer.Spark.Commerce.Inventory.QuotaAvailability("q", 20, 11, 1, 8);
        var shrunk = await host.Warehouse.StockAsync(FulfilmentHost.ShopName, "OIL-75", 0, new FulfilmentChannelStock { Channel = "shop", ShippedTotal = 4 });
        await host.Stock.ApplyAsync(shrunk, force: false, default);
        host.Shop.Stock[^1].Should().Be((FulfilmentHost.VariantA, 12));
    }

    [Fact]
    public async Task A_recalled_lot_is_traced_to_its_buyers_who_are_mailed_once_GA7()
    {
        var host = new FulfilmentHost(Store);
        await host.SeedAsync();
        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        var order = (await host.Warehouse.OrderAsync(FulfilmentHost.Revision(1)))!;
        await host.Warehouse.ShipAsync(order, 1, "LOT-7", FulfilmentTrackingState.InTransit, ("L1", 2));
        await host.Warehouse.ShipAsync(order, 2, "LOT-8", FulfilmentTrackingState.InTransit, ("L2", 1));
        WaitForIndexing(Store);

        var mailer = new RecordingMailer();
        var services = new ServiceCollection().AddSingleton<ISparkMailer>(mailer).BuildServiceProvider();
        var recalls = host.Recalls(services);

        var buyers = await recalls.TraceAsync("OIL-75", "LOT-7", null, default);
        buyers.Should().ContainSingle();
        buyers[0].OrderNumber.Should().Be(FulfilmentHost.Number);
        buyers[0].Units.Should().Be(2m);
        buyers[0].Email.Should().Be("buyer@example.org");
        (await recalls.TraceAsync("OIL-50", "LOT-7", null, default)).Should().BeEmpty(); // the lot and the SKU on the same line

        var notice = new FulfilmentRecallNotice("recall-1", "OIL-75", "LOT-7", ProductName: TranslatedString.Of("en", "Olive oil 75 cl"));
        var result = await recalls.SendAsync(notice, default);
        result.Mailed.Should().Be(1);
        mailer.Sent.Single().Template.Should().Be("SparkFulfilment/RecallNotice");
        mailer.Sent.Single().To.Should().Be("buyer@example.org");
        (await recalls.SendAsync(notice, default)).Mailed.Should().Be(0); // once per recall and order
    }

    [Fact]
    public async Task A_shop_order_the_shop_ships_itself_is_never_published()
    {
        var host = new FulfilmentHost(Store);
        await host.SeedAsync(shopOrder: s => s.Fulfilment = MintPlayer.Spark.Commerce.Shop.ShopFulfilmentMode.ShopOnly);
        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        (await host.Warehouse.OrderAsync(FulfilmentHost.Revision(1))).Should().BeNull();
        (await host.LoadAsync<FulfilmentLink>(FulfilmentIds.Link(FulfilmentHost.ShopName, FulfilmentHost.Number, null))).Should().BeNull();
    }

    [Fact]
    public async Task An_unpaid_order_is_not_handed_to_the_warehouse()
    {
        var host = new FulfilmentHost(Store);
        await host.SeedAsync(order: o => o.State = OrderState.PaymentPending);
        await host.Sync.SyncOrderAsync(FulfilmentHost.OrderId, default);
        (await host.Warehouse.OrderAsync(FulfilmentHost.Revision(1))).Should().BeNull();
    }

    [Fact]
    public async Task The_stock_feed_backs_the_lines_of_a_warehouse_shop_order_only()
    {
        var host = new FulfilmentHost(Store);
        await host.SeedAsync();
        var feed = new MintPlayer.Spark.Commerce.Fulfilment.Services.FulfilmentStockFeed(Store, Monitor(host.Options));
        var order = (await host.LoadAsync<CheckoutOrder>(FulfilmentHost.OrderId))!;
        (await feed.BacksAsync(order, order.Lines[0], default)).Should().BeTrue();
        var ticket = new CheckoutOrderLine { LineId = "T1", ItemId = "tickets/1", Channel = "shop", IsPhysicalGoods = false };
        (await feed.BacksAsync(order, ticket, default)).Should().BeFalse();

        host.Options.Channels["market"] = new FulfilmentChannelOptions();
        (await feed.BacksAsync(order, order.Lines[0], default)).Should().BeFalse(); // "shop" is not a channel the warehouse ships
    }
}
