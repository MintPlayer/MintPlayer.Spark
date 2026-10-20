using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Commerce.Tests.Checkout;

/// <summary>
/// The refund arithmetic and rules of M9, pure: S21 cumulative shares (partial refunds sum exactly to the line), the
/// discount allocation, policy bounds (D20), the withdrawal and guarantee periods (Reg. 1182/71), the delivery rule
/// (CRD Art. 13(2)) and the return-reason taxonomy (GA8).
/// </summary>
public class RefundRulesTests
{
    private static Money Eur(decimal amount) => new(amount, "EUR");

    private static CheckoutOrder Order(params CheckoutOrderLine[] lines) => new()
    {
        Id = "CheckoutOrders/1", Number = "1", Currency = "EUR", State = OrderState.Confirmed, Lines = [.. lines],
        Total = Money.Sum(lines.Select(l => l.Total), "EUR"),
    };

    private static CheckoutOrderLine Line(string id, int quantity, decimal total, decimal fee = 0, bool goods = false) => new()
    {
        LineId = id, ItemId = "items/" + id, Channel = "shop", Quantity = quantity, Total = Eur(total), IsPhysicalGoods = goods,
        Fees = fee == 0 ? [] : [new CheckoutOrderFee { Code = "service", Amount = Eur(fee), Mandatory = true }],
    };

    private static CheckoutRefundLedger Ledger(CheckoutOrder order) => new()
    {
        OrderId = order.Id!, Components = RefundMath.Components(order),
        Lines = order.Lines.Select(l => new RefundLedgerLine { LineId = l.LineId, ItemId = l.ItemId }).ToList(),
    };

    private static RefundPlan Step(CheckoutOrder order, CheckoutRefundLedger ledger, RefundSpec spec)
    {
        var plan = RefundMath.Plan(order, ledger, spec);
        RefundMath.Apply(ledger, plan);
        return plan;
    }

    [Fact]
    public void Unit_refunds_take_cumulative_shares_so_any_sequence_sums_exactly_to_the_line_S21()
    {
        var order = Order(Line("a", 3, 10.00m));
        var ledger = Ledger(order);
        var amounts = new[] { 1, 1, 1 }.Select(q => Step(order, ledger, new RefundSpec(RefundKind.Cancellation, null, [new RefundLineRequest("a", q)])).Amount).ToList();
        string.Join(",", amounts.Select(a => a.ToMinorUnits())).Should().Be("333,334,333");
        Money.Sum(amounts, "EUR").Should().Be(Eur(10.00m));
        ledger.Lines.Single().QuantityReturned.Should().Be(3);
        var more = TestThrows.Throws<CheckoutException>(() => RefundMath.Plan(order, ledger, new RefundSpec(RefundKind.Cancellation, null, [new RefundLineRequest("a", 1)])));
        more.Code.Should().Be(CheckoutRefusals.InvalidQuantity);
    }

    [Fact]
    public void An_amount_refund_then_unit_refunds_never_exceed_the_line()
    {
        var order = Order(Line("a", 2, 50.00m), Line("b", 1, 30.00m));
        var ledger = Ledger(order);
        // A 16.00 goodwill spread over what each line can still refund: 50/80 and 30/80.
        var goodwill = Step(order, ledger, new RefundSpec(RefundKind.Return, ReturnReasonCode.SellerAllowed, [], Amount: Eur(16.00m)));
        string.Join(",", goodwill.Parts.Select(p => p.Gross.ToMinorUnits())).Should().Be("1000,600");
        var first = Step(order, ledger, new RefundSpec(RefundKind.Cancellation, null, [new RefundLineRequest("a", 1)]));
        first.Amount.Should().Be(Eur(15.00m)); // share 25.00 - 10.00 already settled
        var rest = Step(order, ledger, new RefundSpec(RefundKind.Cancellation, null, [new RefundLineRequest("a", 1), new RefundLineRequest("b", 1)]));
        rest.Amount.Should().Be(Eur(25.00m + 24.00m));
        ledger.Components.Sum(c => c.Settled.ToMinorUnits()).Should().Be(8000);
    }

    [Fact]
    public void An_order_discount_enters_the_line_prices_through_the_share_stored_on_each_line()
    {
        // Checkout stored Money.Allocate(-100, [1000, 2000]) = -33 / -67 on the lines at pricing (S21).
        var a = Line("a", 1, 10.00m);
        a.OrderDiscount = Eur(-0.33m);
        var b = Line("b", 1, 20.00m);
        b.OrderDiscount = Eur(-0.67m);
        var order = Order(a, b);
        order.Extras.Add(new CheckoutOrderExtra { Code = "promo", Kind = PricedExtraKind.Discount, Amount = Eur(-1.00m) });
        var components = RefundMath.Components(order);
        components.Single(c => c.Key == RefundMath.PriceKey("a")).Gross.Should().Be(Eur(9.67m));
        components.Single(c => c.Key == RefundMath.PriceKey("b")).Gross.Should().Be(Eur(19.33m));
    }

    /// <summary>
    /// Red before the fix: the refund re-spread the discount over the lines left after a refused line, so the remaining
    /// lines' price parts took the refused line's share too and a full refund paid back less than was charged for them.
    /// </summary>
    [Fact]
    public void A_refused_lines_discount_share_leaves_with_it_and_a_full_refund_returns_what_the_rest_was_charged()
    {
        var a = Line("a", 2, 20.00m, fee: 2.00m);
        a.OrderDiscount = Eur(-1.50m);
        var b = Line("b", 1, 10.00m);
        b.OrderDiscount = Eur(-0.50m);
        b.Status = OrderLineStatus.Refused;
        var order = Order(a, b);
        order.Extras.Add(new CheckoutOrderExtra { Code = "promo", Kind = PricedExtraKind.Discount, Amount = Eur(-2.00m) });
        order.Total = Eur(28.00m);
        var ledger = Ledger(order);
        ledger.Components.Single(c => c.Key == RefundMath.PriceKey("a")).Gross.Should().Be(Eur(16.50m)); // 20.00 − 2.00 fee − 1.50

        var plan = Step(order, ledger, new RefundSpec(RefundKind.Cancellation, null, [new RefundLineRequest("a", 2)], RefundFees: true));
        // Charged for a: 20.00 − 1.50 = 18.50; b's 9.50 went back at confirmation (OrderLifecycle refunds line.Charged()).
        plan.Amount.Should().Be(a.Charged());
        (plan.Amount + b.Charged()).Should().Be(order.Total);
    }

    [Fact]
    public void A_partial_line_refund_takes_its_cumulative_share_of_the_discounted_price()
    {
        var a = Line("a", 3, 30.00m);
        a.OrderDiscount = Eur(-3.00m);
        var order = Order(a);
        order.Extras.Add(new CheckoutOrderExtra { Code = "promo", Kind = PricedExtraKind.Discount, Amount = Eur(-3.00m) });
        var ledger = Ledger(order);
        var first = Step(order, ledger, new RefundSpec(RefundKind.Cancellation, null, [new RefundLineRequest("a", 1)]));
        first.Amount.Should().Be(Eur(9.00m)); // (30.00 − 3.00) / 3, not 10.00
        var rest = Step(order, ledger, new RefundSpec(RefundKind.Cancellation, null, [new RefundLineRequest("a", 2)]));
        (first.Amount + rest.Amount).Should().Be(a.Charged());
    }

    [Fact]
    public void Fees_are_refunded_only_when_asked_and_a_cancellation_fee_is_kept_from_the_price_D20()
    {
        var order = Order(Line("a", 2, 42.00m, fee: 2.00m));
        var withoutFees = Step(order, Ledger(order), new RefundSpec(RefundKind.Cancellation, null, [new RefundLineRequest("a", 2)], FeeBasisPoints: 1000));
        withoutFees.Amount.Should().Be(Eur(36.00m)); // 40.00 price, 10 % kept, fee 2.00 not refunded
        withoutFees.Deducted.Should().Be(Eur(4.00m));
        var withFees = Step(order, Ledger(order), new RefundSpec(RefundKind.Cancellation, null, [new RefundLineRequest("a", 2)], RefundFees: true));
        withFees.Amount.Should().Be(Eur(42.00m));
    }

    [Fact]
    public void A_withdrawal_refunds_the_standard_delivery_only_when_the_last_goods_go_back_CRD_13_2()
    {
        var order = Order(Line("a", 1, 20.00m, goods: true), Line("b", 1, 30.00m, goods: true));
        order.Extras.Add(new CheckoutOrderExtra { Code = "delivery", Kind = PricedExtraKind.Other, Amount = Eur(6.95m) });
        var ledger = Ledger(order);
        ledger.Components = RefundMath.Components(order, new Dictionary<string, Money?> { ["delivery"] = Eur(4.95m) });
        var spec = (string line) => new RefundSpec(RefundKind.Withdrawal, ReturnReasonCode.Withdrawal, [new RefundLineRequest(line, 1)], RefundFees: true, Delivery: DeliveryRefund.Standard);
        Step(order, ledger, spec("a")).Amount.Should().Be(Eur(20.00m));
        Step(order, ledger, spec("b")).Amount.Should().Be(Eur(30.00m + 4.95m)); // the 2.00 express surcharge is kept
    }

    [Fact]
    public void A_pro_rata_partial_withdrawal_refunds_delivery_in_proportion()
    {
        var order = Order(Line("a", 2, 20.00m, goods: true));
        order.Extras.Add(new CheckoutOrderExtra { Code = "delivery", Kind = PricedExtraKind.Other, Amount = Eur(5.00m) });
        var ledger = Ledger(order);
        var spec = new RefundSpec(RefundKind.Withdrawal, ReturnReasonCode.Withdrawal, [new RefundLineRequest("a", 1)], Delivery: DeliveryRefund.Standard, PartialDelivery: PartialDeliveryRefund.ProRata);
        Step(order, ledger, spec).Amount.Should().Be(Eur(12.50m));
        Step(order, ledger, spec).Amount.Should().Be(Eur(12.50m));
    }

    [Fact]
    public void A_deduction_larger_than_the_share_is_refused()
    {
        var order = Order(Line("a", 1, 10.00m, goods: true));
        var refused = TestThrows.Throws<CheckoutException>(() => RefundMath.Plan(order, Ledger(order), new RefundSpec(RefundKind.Withdrawal, ReturnReasonCode.Withdrawal,
            [new RefundLineRequest("a", 1)], Deductions: new Dictionary<string, Money> { ["a"] = Eur(11.00m) })));
        refused.Code.Should().Be(RefundRefusals.DeductionTooHigh);
    }

    [Fact]
    public void A_seller_policy_is_clamped_to_the_channel_bounds_D20()
    {
        var bounds = new RefundPolicyBounds { Allowed = [RefundPolicyKind.None, RefundPolicyKind.WithFee], MaxFeeBasisPoints = 1500 };
        RefundMath.Clamp(new RefundPolicy(RefundPolicyKind.WithFee, FeeBasisPoints: 5000), bounds).FeeBasisPoints.Should().Be(1500);
        RefundMath.Clamp(new RefundPolicy(RefundPolicyKind.UntilDaysBefore, DaysBefore: 3), bounds).Kind.Should().Be(RefundPolicyKind.None);
        RefundMath.Clamp(null, bounds).Kind.Should().Be(RefundPolicyKind.None);
        var days = new RefundPolicyBounds { DaysBefore = new IntBounds { Default = 7, Min = 2, Max = 30 } };
        RefundMath.Clamp(new RefundPolicy(RefundPolicyKind.UntilDaysBefore, DaysBefore: 60), days).DaysBefore.Should().Be(30);
    }

    [Fact]
    public void A_cancellation_follows_the_policy_and_never_after_the_date()
    {
        var now = DateTimeOffset.Parse("2026-10-19T10:00:00Z");
        var date = now.AddDays(10);
        RefundMath.CancellationRefusal(RefundPolicy.None, date, now, out _).Should().Be(RefundRefusals.NoCancellation);
        RefundMath.CancellationRefusal(new RefundPolicy(RefundPolicyKind.UntilDaysBefore, DaysBefore: 7), date, now, out _).Should().BeNull();
        RefundMath.CancellationRefusal(new RefundPolicy(RefundPolicyKind.UntilDaysBefore, DaysBefore: 14), date, now, out _).Should().Be(RefundRefusals.CancellationClosed);
        RefundMath.CancellationRefusal(new RefundPolicy(RefundPolicyKind.WithFee, FeeBasisPoints: 500), date, now, out var fee).Should().BeNull();
        fee.Should().Be(500);
        RefundMath.CancellationRefusal(new RefundPolicy(RefundPolicyKind.WithFee, FeeBasisPoints: 500), now.AddDays(-1), now, out _).Should().Be(RefundRefusals.ServicePassed);
    }

    [Fact]
    public void Periods_start_the_day_after_and_end_at_the_end_of_the_last_day_moved_off_a_weekend_Reg_1182_71()
    {
        var zone = SparkTimeZones.FindOrUtc("UTC");
        // Delivered Monday 2026-10-05 15:00: day 1 is Tuesday the 6th, day 14 is Monday the 19th; ends at the 20th 00:00.
        RefundMath.PeriodEnd(DateTimeOffset.Parse("2026-10-05T15:00:00Z"), 14, zone).Should().Be(DateTimeOffset.Parse("2026-10-20T00:00:00Z"));
        // Delivered Saturday 2026-10-03: day 14 is Saturday the 17th, moved to Monday the 19th.
        RefundMath.PeriodEnd(DateTimeOffset.Parse("2026-10-03T09:00:00Z"), 14, zone).Should().Be(DateTimeOffset.Parse("2026-10-20T00:00:00Z"));
        RefundMath.PeriodEnd(DateTimeOffset.Parse("2026-10-03T09:00:00Z"), 14, zone, moveOffWeekend: false).Should().Be(DateTimeOffset.Parse("2026-10-18T00:00:00Z"));
    }

    [Fact]
    public void The_withdrawal_period_starts_at_the_last_delivery_of_the_order_CRD_9_2_b_i()
    {
        var order = Order(Line("a", 1, 10m, goods: true), Line("b", 1, 10m, goods: true), Line("t", 1, 10m));
        var ledger = Ledger(order);
        RefundMath.WithdrawalPeriodStart(order, ledger).HasValue.Should().BeFalse();
        ledger.Lines[0].DeliveredAt = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        RefundMath.WithdrawalPeriodStart(order, ledger).HasValue.Should().BeFalse(); // b is not delivered yet: the right is live
        ledger.Lines[1].DeliveredAt = DateTimeOffset.Parse("2026-10-04T10:00:00Z");
        RefundMath.WithdrawalPeriodStart(order, ledger).Should().Be(DateTimeOffset.Parse("2026-10-04T10:00:00Z"));
    }

    [Theory]
    [InlineData(ReturnReasonCode.Withdrawal, ReturnFlow.Withdrawal, DeliveryRefund.Standard, ReturnDisposition.Restock)]
    [InlineData(ReturnReasonCode.NonConformity, ReturnFlow.GuaranteeClaim, DeliveryRefund.Full, ReturnDisposition.Inspect)]
    [InlineData(ReturnReasonCode.WrongItem, ReturnFlow.SellerReturn, DeliveryRefund.Full, ReturnDisposition.Restock)]
    [InlineData(ReturnReasonCode.DamagedInTransit, ReturnFlow.SellerReturn, DeliveryRefund.Full, ReturnDisposition.Quarantine)]
    [InlineData(ReturnReasonCode.SellerAllowed, ReturnFlow.SellerReturn, DeliveryRefund.None, ReturnDisposition.Restock)]
    [InlineData(ReturnReasonCode.Other, ReturnFlow.SellerReturn, DeliveryRefund.None, ReturnDisposition.Inspect)]
    public void The_return_reason_picks_the_flow_the_delivery_rule_and_the_disposition_GA8(ReturnReasonCode reason, ReturnFlow flow, DeliveryRefund delivery, ReturnDisposition disposition)
    {
        var rules = ReturnReasons.For(reason);
        rules.Flow.Should().Be(flow);
        rules.Delivery.Should().Be(delivery);
        rules.DefaultDisposition.Should().Be(disposition);
        if (reason == ReturnReasonCode.Withdrawal)
            rules.InventoryReason.Should().Be(ReturnReason.Withdrawal);
    }

    [Fact]
    public void A_partial_inventory_return_scales_items_and_names_the_per_buyer_counts_I4()
    {
        var line = Line("a", 4, 40m);
        line.Capacity = new CheckoutCapacitySnapshot { Items = [new ItemLine("items/a", 4)] };
        line.Limits = [new LimitLine("limits/x", 6, 4)];
        var partial = RefundMath.InventoryReturnFor(line, new RefundUnits { LineId = "a", Quantity = 1 }, 0, "r1", ReturnReason.Refund);
        partial.Items!.Single().N.Should().Be(1);
        partial.Limits!.Single().N.Should().Be(1);
        partial.ReturnStock.Should().BeTrue();
        line.ShippedAt = DateTimeOffset.UnixEpoch;
        var everything = RefundMath.InventoryReturnFor(line, new RefundUnits { LineId = "a", Quantity = 4 }, 0, "r2", ReturnReason.Withdrawal);
        everything.Items.Should().BeNull(); // the whole claim
        everything.ReturnStock.Should().BeFalse(); // shipped goods: quota only at restock
    }

    [Fact]
    public void A_provider_reference_names_the_order_and_the_refund()
    {
        var reference = RefundEngine.ProviderReference("CheckoutOrders/7-A", "c-1");
        RefundEngine.TryParseReference(reference, out var orderId, out var refundId).Should().BeTrue();
        orderId.Should().Be("CheckoutOrders/7-A");
        refundId.Should().Be("c-1");
        RefundEngine.TryParseReference("CheckoutOrders/7-A:superfluous:x", out _, out _).Should().BeFalse();
    }
}
