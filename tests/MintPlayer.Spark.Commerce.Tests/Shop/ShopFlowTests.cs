using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Commerce.Shop;
using MintPlayer.Spark.Commerce.Shop.Catalog;
using MintPlayer.Spark.Commerce.Shop.Contracts;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Commerce.Shop.Orders;
using MintPlayer.Spark.Commerce.Shop.Reviews;
using MintPlayer.Spark.Commerce.Shop.Services;
using MintPlayer.Spark.Commerce.Tests.Checkout;
using MintPlayer.Spark.Commerce.Tests.Events;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Testing;
using static MintPlayer.Spark.Commerce.Tests.Events.EventFixtures;

namespace MintPlayer.Spark.Commerce.Tests.Shop;

/// <summary>Records what the shop reports to Checkout's refunds (delivery, goods back).</summary>
internal sealed class RecordingRefunds : ISparkCheckoutRefunds
{
    public List<(string OrderId, string LineId, DateTimeOffset At)> Delivered { get; } = [];

    public List<GoodsBackRegistration> GoodsBack { get; } = [];

    public Task MarkDeliveredAsync(string orderId, string lineId, DateTimeOffset deliveredAt, CancellationToken cancellationToken = default)
    {
        Delivered.Add((orderId, lineId, deliveredAt));
        return Task.CompletedTask;
    }

    public Task RegisterGoodsBackAsync(GoodsBackRegistration registration, CancellationToken cancellationToken = default)
    {
        GoodsBack.Add(registration);
        return Task.CompletedTask;
    }
}

internal sealed class SignedInUser(string? id) : ISparkCurrentUser
{
    public string? Id => id;

    public bool IsAuthenticated => id is not null;
}

/// <summary>An access control that answers every <c>IsAllowedAsync</c> with <see cref="Allowed"/>.</summary>
internal class FixedAccess : DispatchProxy
{
    public bool Allowed { get; set; }

    public static IAccessControl Create(bool allowed)
    {
        var proxy = DispatchProxy.Create<IAccessControl, FixedAccess>();
        ((FixedAccess)(object)proxy).Allowed = allowed;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod!.ReturnType == typeof(Task<bool>) ? Task.FromResult(Allowed) : throw new NotSupportedException(targetMethod.Name);
}

/// <summary>
/// The shop's order and review flows against RavenDB (M12): delivery is reported to Checkout per line once all its units
/// arrived (D44, CRD Art. 9(2)(b)); a return's first carrier scan is the proof of dispatch and its delivery the receipt
/// (CRD Art. 13(3)); only a delivered line can be reviewed, once, and a pre-moderated review counts once published (GC5).
/// </summary>
public class ShopFlowTests : SparkTestDriver
{
    private readonly EventTestClock clock = new(Now);
    private readonly RecordingRefunds refunds = new();
    private readonly FakeCheckout checkout = new();
    private readonly SparkCommerceShopOptions options = new();

    private ShopAccess Access(string? userId = "users/1-A", bool allowed = false)
        => new(new SignedInUser(userId), NullService<ISparkOrganizations>.Create(), FixedAccess.Create(allowed));

    private ShopOrders Orders()
        => new(Store, checkout, refunds, null!, null!, Access(), null!, Monitor(options), clock, NullLogger<ShopOrders>.Instance);

    private ShopReviews Reviews(bool moderator = false)
        => new(Store, checkout, Access(allowed: moderator), Monitor(options), clock);

    private static ShopOrder Shipped()
    {
        var order = ShopShippingTests.Order();
        order.State = EShopOrderState.Shipped;
        order.Lines[0].ShippedQuantity = 2;
        order.Lines[1].ShippedQuantity = 1;
        order.Shipments.Add(new ShopOrderShipment { ShipmentId = "Shipments/S1", Lines = [new() { LineId = "L1", Quantity = 1 }, new() { LineId = "L2", Quantity = 1 }] });
        order.Shipments.Add(new ShopOrderShipment { ShipmentId = "Shipments/S2", Lines = [new() { LineId = "L1", Quantity = 1 }] });
        return order;
    }

    private async Task StoreAsync(params object[] documents)
    {
        using var session = Store.OpenAsyncSession();
        foreach (var document in documents)
            await session.StoreAsync(document);
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task Delivery_is_reported_per_line_once_all_its_units_arrived()
    {
        var order = Shipped();
        await StoreAsync(order);
        var orders = Orders();

        var first = Now.AddDays(1);
        await orders.MarkDeliveredAsync(order.OrderId, null, "Shipments/S1", first);
        refunds.Delivered.Should().Equal((order.OrderId, "L2", first));
        (await orders.GetAsync(order.OrderId, null))!.State.Should().Be(EShopOrderState.Shipped);

        // The last parcel of L1: the withdrawal period of the order starts here (Checkout takes the latest, D44).
        var last = Now.AddDays(3);
        await orders.MarkDeliveredAsync(order.OrderId, null, "Shipments/S2", last);
        refunds.Delivered.Should().HaveCount(2);
        refunds.Delivered[1].Should().Be((order.OrderId, "L1", last));
        var stored = (await orders.GetAsync(order.OrderId, null))!;
        stored.State.Should().Be(EShopOrderState.Delivered);
        stored.DeliveredAt.HasValue.Should().BeTrue();
        stored.DeliveredAt!.Value.Should().Be(last);

        // A carrier event delivered twice reports nothing new.
        await orders.MarkDeliveredAsync(order.OrderId, null, "Shipments/S2", last.AddHours(1));
        refunds.Delivered.Should().HaveCount(2);
    }

    private static Shipment ReturnShipment(TrackingStatus status, params (TrackingStatus Status, DateTimeOffset At)[] events) => new()
    {
        Id = "Shipments/R1",
        Reference = "ShopOrders/CheckoutOrders/1-A/platform",
        Direction = ShipmentDirection.Return,
        ReturnOf = "Shipments/S1",
        Status = status,
        Events = events.Select((e, i) => new TrackingEventRecord { EventId = "e" + i, Status = e.Status, OccurredAtUtc = e.At.UtcDateTime }).ToList(),
    };

    [Fact]
    public async Task Return_tracking_registers_the_proof_of_dispatch_then_the_receipt()
    {
        var order = Shipped();
        order.Returns.Add(new ShopOrderReturn { ReturnId = "R1", Reason = ReturnReasonCode.Withdrawal, ReturnShipmentId = "Shipments/R1", Lines = [new() { LineId = "L2", Quantity = 1 }] });
        var orders = Orders();

        // CRD Art. 13(3): the carrier's first scan is the buyer's proof of dispatch.
        var scanned = Now.AddDays(5);
        await orders.OnReturnTrackingAsync(order, ReturnShipment(TrackingStatus.InTransit, (TrackingStatus.Labelled, Now), (TrackingStatus.InTransit, scanned)), proofOfDispatch: true, CancellationToken.None);
        refunds.GoodsBack.Should().ContainSingle();
        refunds.GoodsBack[0].Kind.Should().Be(GoodsBackKind.ProofOfDispatch);
        refunds.GoodsBack[0].At.Should().Be(scanned);
        order.Returns[0].ProofOfDispatchAt.HasValue.Should().BeTrue();

        // Its delivery to the seller is the receipt (Returns:ReceiptOn = CarrierDelivery), inspected later.
        var arrived = Now.AddDays(7);
        await orders.OnReturnTrackingAsync(order, ReturnShipment(TrackingStatus.Delivered, (TrackingStatus.InTransit, scanned), (TrackingStatus.Delivered, arrived)), proofOfDispatch: false, CancellationToken.None);
        refunds.GoodsBack.Should().HaveCount(2);
        refunds.GoodsBack[1].Kind.Should().Be(GoodsBackKind.Received);
        refunds.GoodsBack[1].Disposition.Should().Be(ReturnDisposition.Inspect);
        order.Returns[0].ReceivedAt.HasValue.Should().BeTrue();
    }

    [Fact]
    public async Task With_staff_inspection_the_carrier_delivery_registers_no_receipt()
    {
        options.Returns.ReceiptOn = ShopReceiptMoment.StaffInspection;
        var order = Shipped();
        order.Returns.Add(new ShopOrderReturn { ReturnId = "R1", Reason = ReturnReasonCode.NonConformity, ReturnShipmentId = "Shipments/R1" });
        await Orders().OnReturnTrackingAsync(order, ReturnShipment(TrackingStatus.Delivered, (TrackingStatus.Delivered, Now)), proofOfDispatch: false, CancellationToken.None);
        refunds.GoodsBack.Should().BeEmpty();
        order.Returns[0].ArrivedAt.HasValue.Should().BeTrue();
        order.Returns[0].ReceivedAt.HasValue.Should().BeFalse();
    }

    private async Task<ShopOrder> SeedReviewableAsync()
    {
        var product = ShopRulesTests.Goods(p => p.IsPublished = true);
        var order = Shipped();
        order.Lines[0].ProductId = product.Id!;
        order.Lines[1].ProductId = product.Id!;
        order.Lines[0].DeliveredAt = Now.AddDays(-2);
        await StoreAsync(product, order);
        checkout.Order = new CheckoutOrder
        {
            Id = order.OrderId,
            Number = order.OrderNumber,
            Lines =
            [
                new CheckoutOrderLine { LineId = "L1", ItemId = ShopItemIds.For("ShopVariants/1-A"), IsPhysicalGoods = true },
                new CheckoutOrderLine { LineId = "L2", ItemId = ShopItemIds.For("ShopVariants/2-A"), IsPhysicalGoods = true },
            ],
        };
        return order;
    }

    [Fact]
    public async Task Only_a_delivered_line_of_the_callers_own_order_can_be_reviewed_once()
    {
        await SeedReviewableAsync();
        var reviews = Reviews();

        var result = await reviews.SubmitAsync(new SparkShopReviewRequest("SO-1", "L1", 4, "Fruity and fresh.", DisplayName: "Ann"));
        result.Status.Should().Be(nameof(EShopReviewStatus.Pending));

        (await TestThrows.ThrowsAsync<ShopException>(() => reviews.SubmitAsync(new SparkShopReviewRequest("SO-1", "L1", 5, "Again."))))
            .Code.Should().Be(ShopRefusals.AlreadyReviewed);
        (await TestThrows.ThrowsAsync<ShopException>(() => reviews.SubmitAsync(new SparkShopReviewRequest("SO-1", "L2", 5, "Not delivered yet."))))
            .Code.Should().Be(ShopRefusals.NotVerified);
        (await TestThrows.ThrowsAsync<ShopException>(() => reviews.SubmitAsync(new SparkShopReviewRequest("SO-2", "L1", 5, "Not my order."))))
            .Code.Should().Be(ShopRefusals.OrderNotFound);
    }

    [Fact]
    public async Task A_pre_moderated_review_counts_once_a_moderator_publishes_it_and_is_never_refused_for_its_rating()
    {
        await SeedReviewableAsync();
        var submitted = await Reviews().SubmitAsync(new SparkShopReviewRequest("SO-1", "L1", 2, "Too bitter for me."));

        (await Reviews().GetAsync("ShopProducts/1-A"))!.Reviews.Should().BeEmpty();
        WaitForIndexing(Store);
        (await TestThrows.ThrowsAsync<ShopException>(() => Reviews(moderator: false).GetPendingAsync())).Code.Should().Be(ShopRefusals.Forbidden);

        var moderator = Reviews(moderator: true);
        (await moderator.GetPendingAsync()).Should().ContainSingle().Which.Id.Should().Be(submitted.Id);
        (await TestThrows.ThrowsAsync<ShopException>(() => moderator.DecideAsync(new SparkShopReviewDecision(submitted.Id, Publish: false, Reason: "Negative"))))
            .Code.Should().Be(ShopRefusals.ReasonRequired);
        (await moderator.DecideAsync(new SparkShopReviewDecision(submitted.Id, Publish: true))).Status.Should().Be(nameof(EShopReviewStatus.Published));

        WaitForIndexing(Store);
        var page = (await Reviews().GetAsync("ShopProducts/1-A"))!;
        page.Count.Should().Be(1);
        page.Average.HasValue.Should().BeTrue();
        page.Average!.Value.Should().Be(2m);
        page.Distribution.Should().Equal(0, 1, 0, 0, 0);
        page.Reviews.Should().ContainSingle().Which.Verified.Should().BeTrue();
        page.PreModerated.Should().BeTrue();
    }
}
