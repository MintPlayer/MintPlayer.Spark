using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Commerce.Tests.Checkout;

/// <summary>A stock feed that owns every goods line (a warehouse ships all of them).</summary>
internal sealed class GoodsStockFeed : ICheckoutStockFeed
{
    public Task<bool> BacksAsync(CheckoutOrder order, CheckoutOrderLine line, CancellationToken cancellationToken) => Task.FromResult(line.IsPhysicalGoods);
}

/// <summary>
/// Checkout's stock-feed seam (warehouse §4.4): for a line a warehouse's feed backs, Checkout never puts quota units back
/// on sale itself. A refund before shipment keeps them sold until the warehouse confirmed the cancellation (rule 1, the
/// feed restocks then), and goods received back are restocked by the warehouse's on-hand, not by Checkout (rule 4).
/// </summary>
public class RefundStockFeedTests : SparkTestDriver
{
    [Fact]
    public async Task A_refund_before_shipment_of_a_fed_line_keeps_its_units_sold()
    {
        var (host, _, _) = RefundHost.Build(Store, stockFeed: new GoodsStockFeed());
        await host.ItemAsync("oil/a", "shop", 10m);
        RefundHost.Goods(host, "oil/a");
        var order = await RefundHost.PaidOrderAsync(host, "visitor-f1", ("oil/a", 2));
        (await host.AvailableAsync("oil/a")).Should().Be(8);

        // An early withdrawal (nothing shipped yet) refunds at once; the units stay sold until the warehouse confirms.
        var refunds = host.Get<RefundService>();
        var receipt = await refunds.WithdrawAsync(await RefundHost.OrderAsync(Store, order.Id!), null, null, null, null, ReturnSource.Online, default, null, confirm: true, default);
        receipt.Withheld.Should().BeFalse();
        (await host.AvailableAsync("oil/a")).Should().Be(8);
        var ledger = await RefundHost.LedgerAsync(Store, order.Id!);
        ledger.Refunds.Single().Lines.Single().StockReturned.Should().BeFalse();

        // The feed restocks it once the cancellation is confirmed (Checkout's inventory return id, exactly once).
        var line = order.Lines.Single();
        var returnId = $"{order.Id}:{ledger.Refunds.Single().RefundId}:{line.LineId}";
        (await host.Inventory.RestockAsync(line.ClaimId!, returnId)).Succeeded.Should().BeTrue();
        (await host.AvailableAsync("oil/a")).Should().Be(10);
    }

    [Fact]
    public async Task Without_a_feed_the_same_refund_returns_the_units_at_once()
    {
        var (host, _, _) = RefundHost.Build(Store);
        await host.ItemAsync("oil/b", "shop", 10m);
        RefundHost.Goods(host, "oil/b");
        var order = await RefundHost.PaidOrderAsync(host, "visitor-f2", ("oil/b", 2));
        await host.Get<RefundService>().WithdrawAsync(await RefundHost.OrderAsync(Store, order.Id!), null, null, null, null, ReturnSource.Online, default, null, confirm: true, default);
        (await host.AvailableAsync("oil/b")).Should().Be(10);
    }

    [Fact]
    public async Task Goods_received_back_on_a_fed_line_are_not_restocked_by_Checkout()
    {
        var (host, provider, _) = RefundHost.Build(Store, stockFeed: new GoodsStockFeed());
        await host.ItemAsync("oil/c", "shop", 10m);
        RefundHost.Goods(host, "oil/c");
        var order = await RefundHost.PaidOrderAsync(host, "visitor-f3", ("oil/c", 2));
        var line = order.Lines.Single();
        await host.Get<ISparkCheckout>().MarkShippedAsync(order.Id!, line.LineId, host.Clock.Now);
        var refunds = host.Get<RefundService>();
        await refunds.MarkDeliveredAsync(order.Id!, line.LineId, host.Clock.Now);
        await refunds.WithdrawAsync(await RefundHost.OrderAsync(Store, order.Id!), null, null, null, null, ReturnSource.Online, default, null, confirm: true, default);
        var withdrawal = (await RefundHost.LedgerAsync(Store, order.Id!)).Returns.Single();

        await refunds.RegisterGoodsBackAsync(new GoodsBackRegistration(order.Id!, withdrawal.ReturnId, GoodsBackKind.Received, host.Clock.Now, ReturnDisposition.Restock));
        provider.Executed.Should().HaveCount(1); // the withheld refund is paid (CRD Art. 13(3))
        (await host.AvailableAsync("oil/c")).Should().Be(8); // rule 4: the warehouse's restock raises its on-hand instead
    }

    [Fact]
    public async Task Undelivered_units_are_refunded_at_their_charged_amount_and_kept_sold_for_the_feed()
    {
        var (host, provider, _) = RefundHost.Build(Store, stockFeed: new GoodsStockFeed());
        await host.ItemAsync("oil/e", "shop", 10m);
        RefundHost.Goods(host, "oil/e");
        var order = await RefundHost.PaidOrderAsync(host, "visitor-f5", ("oil/e", 2));
        var line = order.Lines.Single();
        var refunds = host.Get<RefundService>();

        // The warehouse cancelled one of two units: that unit only, the delivery is kept (one unit is still delivered).
        var refundId = await refunds.RefundUndeliveredAsync(new UndeliveredRefund(order.Id!, "warehouse:L1=1", [new UndeliveredLine(line.LineId, 1)]));
        refundId.Should().NotBeNull();
        provider.Executed.Values.Single().Amount.Should().Be(Money.FromMinorUnits(line.Charged().ToMinorUnits() / 2, line.Charged().Currency));
        (await host.AvailableAsync("oil/e")).Should().Be(8); // the feed restocks it, not Checkout

        // The same fact again refunds nothing.
        (await refunds.RefundUndeliveredAsync(new UndeliveredRefund(order.Id!, "warehouse:L1=1", [new UndeliveredLine(line.LineId, 1)]))).Should().Be(refundId);
        provider.Executed.Should().HaveCount(1);
    }

    [Fact]
    public async Task When_nothing_of_the_order_ships_the_delivery_cost_is_refunded_too()
    {
        var (host, provider, _) = RefundHost.Build(Store, stockFeed: new GoodsStockFeed());
        await host.ItemAsync("oil/f", "shop", 10m);
        RefundHost.Goods(host, "oil/f");
        var order = await RefundHost.PaidOrderAsync(host, "visitor-f6", ("oil/f", 2));
        var line = order.Lines.Single();
        var delivery = order.Extras.Single(e => e.Code == "delivery").Amount;

        await host.Get<RefundService>().RefundUndeliveredAsync(new UndeliveredRefund(order.Id!, "warehouse:L1=2", [new UndeliveredLine(line.LineId, 2)]));
        // CRD Art. 18 + Art. 13(1) (unconfirmed): everything paid, the full delivery cost included.
        provider.Executed.Values.Single().Amount.Should().Be(line.Charged() + delivery);
    }

    [Fact]
    public async Task A_released_withheld_refund_is_paid_without_a_receipt_date_WD12()
    {
        var (host, provider, _) = RefundHost.Build(Store, stockFeed: new GoodsStockFeed());
        await host.ItemAsync("oil/d", "shop", 10m);
        RefundHost.Goods(host, "oil/d");
        var order = await RefundHost.PaidOrderAsync(host, "visitor-f4", ("oil/d", 1));
        var line = order.Lines.Single();
        await host.Get<ISparkCheckout>().MarkShippedAsync(order.Id!, line.LineId, host.Clock.Now);
        var refunds = host.Get<RefundService>();
        await refunds.MarkDeliveredAsync(order.Id!, line.LineId, host.Clock.Now);
        await refunds.WithdrawAsync(await RefundHost.OrderAsync(Store, order.Id!), null, null, null, null, ReturnSource.Online, default, null, confirm: true, default);
        var withdrawal = (await RefundHost.LedgerAsync(Store, order.Id!)).Returns.Single();
        provider.Executed.Should().BeEmpty();

        await refunds.RegisterGoodsBackAsync(new GoodsBackRegistration(order.Id!, withdrawal.ReturnId, GoodsBackKind.Released, host.Clock.Now));
        provider.Executed.Should().HaveCount(1);
        var released = (await RefundHost.LedgerAsync(Store, order.Id!)).Returns.Single();
        released.Status.Should().Be(ReturnStatus.Refunded);
        released.GoodsReceivedAt.HasValue.Should().BeFalse();
        released.ProofOfDispatchAt.HasValue.Should().BeFalse();
    }
}
