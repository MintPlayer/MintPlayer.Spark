using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Contracts;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Commerce.Tests.Checkout;

/// <summary>
/// M9 against RavenDB and the real inventory engine: the unconditional withdrawal (GA1) with its withheld refund and the
/// standard delivery cost, cancellation within the seller's policy (D20), a guarantee termination (GA8), and the mass
/// refund of spike S15 (reduced per round 4): a crash after the provider executed a refund, resume, no double refund,
/// exact counters, one <c>RefundIssued</c> per refund (the credit-note input), counters returned exactly once (I4).
/// </summary>
public class RefundFlowTests : SparkTestDriver
{
    // ---- withdrawal (GA1) -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_withdrawal_needs_no_approval_is_withheld_until_proof_of_dispatch_and_refunds_the_standard_delivery()
    {
        var (host, provider, _) = RefundHost.Build(Store);
        await host.ItemAsync("shirts/m", "shop", 20m);
        RefundHost.Goods(host, "shirts/m");
        var order = await RefundHost.PaidOrderAsync(host, "visitor-w", ("shirts/m", 2));
        order.Extras.Single(e => e.Code == "delivery").Amount.Should().Be(new Money(6.95m, "EUR"));
        var refunds = host.Get<RefundService>();
        var checkout = host.Get<ISparkCheckout>();
        var line = order.Lines.Single();
        await checkout.MarkShippedAsync(order.Id!, line.LineId, host.Clock.Now);
        await refunds.MarkDeliveredAsync(order.Id!, line.LineId, host.Clock.Now);
        (await host.AvailableAsync("shirts/m")).Should().Be(8);

        var view = await refunds.ViewAsync(await RefundHost.OrderAsync(Store, order.Id!), default);
        view.Withdrawal.Available.Should().BeTrue();
        view.Withdrawal.ButtonKey.Should().Be("commerceCheckout.withdrawal.button");
        view.Withdrawal.Deadline.HasValue.Should().BeTrue();

        // The confirmation step: a preview records nothing.
        var preview = await refunds.WithdrawAsync(await RefundHost.OrderAsync(Store, order.Id!), null, null, null, null, ReturnSource.Online, default, null, confirm: false, default);
        preview.Confirmed.Should().BeFalse();
        preview.Lines.Single().Eligible.Should().BeTrue();
        (await RefundHost.LedgerAsync(Store, order.Id!)).Returns.Should().BeEmpty();

        var receipt = await refunds.WithdrawAsync(await RefundHost.OrderAsync(Store, order.Id!), null, null, null, null, ReturnSource.Online, default, null, confirm: true, default);
        receipt.Confirmed.Should().BeTrue();
        receipt.ReceivedAt.Should().Be(host.Clock.Now);
        receipt.Withheld.Should().BeTrue();
        receipt.RefundDueBy.Should().Be(host.Clock.Now.AddDays(14));
        provider.Executed.Should().BeEmpty(); // CRD Art. 13(3): withheld until the goods or proof of dispatch
        host.Events.Events.Select(e => e.Kind).Should().Contain(OrderEventKind.WithdrawalRequested);
        host.Events.Events.Single(e => e.Kind == OrderEventKind.ReturnExpected).Return!.DefaultDisposition.Should().Be(ReturnDisposition.Restock);

        var withdrawal = (await RefundHost.LedgerAsync(Store, order.Id!)).Returns.Single();
        await refunds.RegisterGoodsBackAsync(new GoodsBackRegistration(order.Id!, withdrawal.ReturnId, GoodsBackKind.ProofOfDispatch, host.Clock.Now));
        provider.Executed.Values.Single().Amount.Should().Be(new Money(40m + 4.95m, "EUR")); // the 2.00 surcharge is kept (Art. 13(2))
        (await host.AvailableAsync("shirts/m")).Should().Be(8); // shipped goods: quota only at restock (I4)

        await refunds.RegisterGoodsBackAsync(new GoodsBackRegistration(order.Id!, withdrawal.ReturnId, GoodsBackKind.Received, host.Clock.Now));
        (await host.AvailableAsync("shirts/m")).Should().Be(10);
        await refunds.RegisterGoodsBackAsync(new GoodsBackRegistration(order.Id!, withdrawal.ReturnId, GoodsBackKind.Received, host.Clock.Now));
        (await host.AvailableAsync("shirts/m")).Should().Be(10); // restocked exactly once

        // The provider pays it out: one RefundIssued with the lines and the delivery extra (Invoicing's credit note).
        var recipient = host.Get<RefundPaymentRecipient>();
        var reference = provider.Executed.Keys.Single();
        await recipient.OnRefundChangedAsync(provider.Paid(reference), default);
        await recipient.OnRefundChangedAsync(provider.Paid(reference), default); // redelivered
        var issued = host.Events.Events.Where(e => e.Kind == OrderEventKind.RefundIssued).ToList();
        issued.Should().HaveCount(1);
        issued[0].Refund!.Lines.Single().Quantity.Should().Be(2);
        issued[0].Refund!.Extras.Single().Gross.Should().Be(new Money(4.95m, "EUR"));
        (await RefundHost.OrderAsync(Store, order.Id!)).Lines.Single().Status.Should().Be(OrderLineStatus.Returned);
    }

    [Fact]
    public async Task Outside_the_period_or_for_a_dated_event_the_withdrawal_is_recorded_but_not_refunded_and_says_why()
    {
        var (host, provider, _) = RefundHost.Build(Store);
        await host.ItemAsync("shirts/l", "shop", 20m);
        RefundHost.Goods(host, "shirts/l");
        await host.ItemAsync("concerts/x", "tickets", 30m);
        RefundHost.Dated(host, "concerts/x", host.Clock.Now.AddDays(30));
        var order = await RefundHost.PaidOrderAsync(host, "visitor-p", ("shirts/l", 1));
        var refunds = host.Get<RefundService>();
        await host.Get<ISparkCheckout>().MarkShippedAsync(order.Id!, order.Lines[0].LineId, host.Clock.Now);
        await refunds.MarkDeliveredAsync(order.Id!, order.Lines[0].LineId, host.Clock.Now);
        host.Clock.Advance(TimeSpan.FromDays(20));

        var view = await refunds.ViewAsync(await RefundHost.OrderAsync(Store, order.Id!), default);
        view.Withdrawal.Available.Should().BeFalse();
        var receipt = await refunds.WithdrawAsync(await RefundHost.OrderAsync(Store, order.Id!), null, "changed my mind", null, null, ReturnSource.Online, default, null, true, default);
        receipt.Lines.Single().IneligibleReason.Should().Be("periodEnded");
        provider.Executed.Should().BeEmpty();
        (await RefundHost.LedgerAsync(Store, order.Id!)).Returns.Single().Status.Should().Be(ReturnStatus.Closed);

        var ticketOrder = await RefundHost.PaidOrderAsync(host, "visitor-t", ("concerts/x", 1));
        var ticketView = await refunds.ViewAsync(ticketOrder, default);
        ticketView.Lines.Single().WithdrawalRight.Should().BeFalse();
        ticketView.Lines.Single().WithdrawalExemption.Should().Be("16(l)");
    }

    [Fact]
    public async Task Staff_register_a_mailed_withdrawal_with_its_receipt_time_and_diminished_value_is_kept_on_receipt()
    {
        var (host, provider, _) = RefundHost.Build(Store);
        await host.ItemAsync("lamps/a", "shop", 50m);
        RefundHost.Goods(host, "lamps/a");
        var order = await RefundHost.PaidOrderAsync(host, "visitor-m", ("lamps/a", 1));
        var refunds = host.Get<RefundService>();
        await host.Get<ISparkCheckout>().MarkShippedAsync(order.Id!, order.Lines[0].LineId, host.Clock.Now);
        var letter = host.Clock.Now.AddHours(-3);
        var receipt = await refunds.WithdrawAsync(await RefundHost.OrderAsync(Store, order.Id!), null, null, null, null, ReturnSource.Mail, letter, "staff-1", true, default);
        receipt.ReceivedAt.Should().Be(letter);

        var ret = (await RefundHost.LedgerAsync(Store, order.Id!)).Returns.Single();
        await refunds.RegisterGoodsBackAsync(new GoodsBackRegistration(order.Id!, ret.ReturnId, GoodsBackKind.Received, host.Clock.Now, ReturnDisposition.Inspect,
            [new DiminishedValue(order.Lines[0].LineId, new Money(5m, "EUR"), "used beyond trying")], "staff-1"));
        provider.Executed.Values.Single().Amount.Should().Be(new Money(45m + 4.95m, "EUR"));
        (await host.AvailableAsync("lamps/a")).Should().Be(9); // Inspect: not back on sale yet
    }

    // ---- cancellation within the seller's policy (D20) ---------------------------------------------------------------

    [Fact]
    public async Task A_cancellation_follows_the_policy_fixed_at_purchase_and_keeps_its_fee()
    {
        var (host, provider, terms) = RefundHost.Build(Store);
        await host.ItemAsync("tickets/c", "tickets", 30m);
        RefundHost.Dated(host, "tickets/c", host.Clock.Now.AddDays(30));
        terms.Terms["tickets/c"] = new RefundTerms(new RefundPolicy(RefundPolicyKind.WithFee, FeeBasisPoints: 1000));
        var order = await RefundHost.PaidOrderAsync(host, "visitor-c", ("tickets/c", 2));
        terms.Terms["tickets/c"] = new RefundTerms(RefundPolicy.None); // a later change never touches a sold order
        var refunds = host.Get<RefundService>();

        var preview = await refunds.CancelAsync(await RefundHost.OrderAsync(Store, order.Id!), null, confirm: false, null, default);
        preview.Amount.Should().Be(new Money(54m, "EUR"));
        preview.Kept.Should().Be(new Money(6m, "EUR"));
        provider.Executed.Should().BeEmpty();

        var done = await refunds.CancelAsync(await RefundHost.OrderAsync(Store, order.Id!), null, confirm: true, null, default);
        done.RefundIds.Should().HaveCount(1);
        provider.Executed.Values.Single().Amount.Should().Be(new Money(54m, "EUR"));
        (await host.AvailableAsync("tickets/c")).Should().Be(10); // I4: both units back
        host.Events.Events.Single(e => e.Kind == OrderEventKind.TicketsRevoked).Revocation!.Lines.Single().Quantity.Should().Be(2);
    }

    // ---- guarantee (GA8) -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_guarantee_termination_needs_a_ground_and_refunds_once_the_goods_are_back()
    {
        var (host, provider, _) = RefundHost.Build(Store);
        await host.ItemAsync("kettles/a", "shop", 40m);
        RefundHost.Goods(host, "kettles/a");
        var order = await RefundHost.PaidOrderAsync(host, "visitor-g", ("kettles/a", 1));
        var refunds = host.Get<RefundService>();
        var lineId = order.Lines[0].LineId;
        await host.Get<ISparkCheckout>().MarkShippedAsync(order.Id!, lineId, host.Clock.Now);
        await refunds.MarkDeliveredAsync(order.Id!, lineId, host.Clock.Now);
        host.Clock.Advance(TimeSpan.FromDays(200)); // long after the withdrawal period, within two years

        var view = await refunds.OpenClaimAsync(await RefundHost.OrderAsync(Store, order.Id!), [new SparkRefundLine(lineId, 1)], "It stopped heating.", GuaranteeRemedy.Repair,
            ReturnSource.Online, null, default);
        var claim = view.Claims.Single();
        claim.Lines.Single().Eligible.Should().BeTrue();

        var noGround = await TestThrows.ThrowsAsync<CheckoutException>(() => refunds.ResolveClaimAsync(order, claim.ClaimId, GuaranteeRemedy.Refund, null, null, null, "staff-1", default));
        noGround.Code.Should().Be(RefundRefusals.GroundRequired);

        await refunds.ResolveClaimAsync(order, claim.ClaimId, GuaranteeRemedy.Refund, GuaranteeGround.PersistsAfterAttempt, null, "second failure", "staff-1", default);
        provider.Executed.Should().BeEmpty(); // Art. 16(3): after the goods come back
        var ret = (await RefundHost.LedgerAsync(Store, order.Id!)).Returns.Single(r => r.ClaimId == claim.ClaimId);
        ret.SellerPaysReturnShipping.Should().BeTrue();
        await refunds.RegisterGoodsBackAsync(new GoodsBackRegistration(order.Id!, ret.ReturnId, GoodsBackKind.Received, host.Clock.Now));
        provider.Executed.Values.Single().Amount.Should().Be(new Money(40m + 6.95m, "EUR")); // the seller's fault: delivery in full
        (await host.AvailableAsync("kettles/a")).Should().Be(9); // non-conformity: Inspect, never restocked by default
        (await RefundHost.LedgerAsync(Store, order.Id!)).Claims.Single().Status.Should().Be(GuaranteeClaimStatus.Resolved);
    }

    // ---- mass refund (D20, spike S15) -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_mass_refund_survives_a_crash_and_resumes_with_no_double_refund_and_exact_counters_S15()
    {
        var (host, provider, _) = RefundHost.Build(Store);
        await host.ItemAsync("events/gala", "tickets", 25m, capacity: 50);
        var orders = new List<CheckoutOrder>();
        for (var i = 0; i < 12; i++)
            orders.Add(await RefundHost.PaidOrderAsync(host, "visitor-s" + i, ("events/gala", 1 + i % 3)));
        var sold = orders.Sum(o => o.Lines.Sum(l => l.Quantity));
        (await host.AvailableAsync("events/gala")).Should().Be(50 - sold);
        // One seller cannot cover its refund (D20).
        var poorOrder = orders[4];

        string massId;
        using (var session = Store.OpenAsyncSession())
        {
            var mass = new CheckoutMassRefund { Mode = MassRefundMode.Refund, Status = MassRefundStatus.Collecting, ItemIds = ["events/gala"], CreatedBy = "finance-1", CreatedAt = host.Clock.Now };
            await session.StoreAsync(mass, "CheckoutMassRefunds/");
            await session.SaveChangesAsync();
            massId = mass.Id!;
        }
        provider.ShortOfBalance.Add(RefundEngine.ProviderReference(poorOrder.Id!, RefundEngine.StableId("m", massId)));
        WaitForIndexing(Store);

        // Run 1 dies after the provider executed the 7th refund, before checkout saved that order.
        provider.CrashAfterCall = 7;
        await TestThrows.ThrowsAsync<InvalidOperationException>(() => host.Get<MassRefunds>().RunAsync(massId, default));
        var afterCrash = await MassAsync(massId);
        afterCrash.Counters.Processed.Should().Be(6);
        afterCrash.Total.Should().Be(12);
        afterCrash.LastError.Should().NotBeNull();

        // Run 2 resumes at the same order: its provider call repeats with the same reference (answered from the first).
        provider.CrashAfterCall = null;
        await host.Get<MassRefunds>().RunAsync(massId, default);
        var mass2 = await MassAsync(massId);
        mass2.Status.Should().Be(MassRefundStatus.Completed);
        mass2.Counters.Processed.Should().Be(12);
        mass2.Counters.Refunds.Should().Be(12);
        mass2.Counters.Pending.Should().Be(11);
        mass2.Counters.Failed.Should().Be(1);
        mass2.Counters.InsufficientBalance.Should().Be(1);
        mass2.Counters.AmountRequested.Should().Be(new Money(25m * sold, "EUR"));
        provider.Executed.Should().HaveCount(11); // no double refund; the short one was never executed
        provider.Calls.Should().Be(13);           // 12 orders + the one repeated after the crash
        (await host.AvailableAsync("events/gala")).Should().Be(50); // every unit back exactly once (I4)
        host.Events.Events.Count(e => e.Kind == OrderEventKind.TicketsRevoked).Should().Be(12);
        foreach (var order in orders)
            (await RefundHost.LedgerAsync(Store, order.Id!)).Refunds.Should().HaveCount(1);

        // A third run changes nothing.
        await host.Get<MassRefunds>().RunAsync(massId, default);
        provider.Calls.Should().Be(13);

        // The provider pays out (webhooks, one redelivered); the seller tops up and finance retries the failed one.
        var recipient = host.Get<RefundPaymentRecipient>();
        foreach (var reference in provider.Executed.Keys.ToList())
            await recipient.OnRefundChangedAsync(provider.Paid(reference), default);
        await recipient.OnRefundChangedAsync(provider.Paid(provider.Executed.Keys.First()), default);
        provider.ShortOfBalance.Clear();
        await host.Get<RefundService>().RetryAsync(poorOrder.Id!, RefundEngine.StableId("m", massId), default);
        await recipient.OnRefundChangedAsync(provider.Paid(RefundEngine.ProviderReference(poorOrder.Id!, RefundEngine.StableId("m", massId))), default);

        var final = await MassAsync(massId);
        final.Counters.Succeeded.Should().Be(12);
        final.Counters.Pending.Should().Be(0);
        final.Counters.Failed.Should().Be(0);
        final.Counters.InsufficientBalance.Should().Be(0);
        var issued = host.Events.Events.Where(e => e.Kind == OrderEventKind.RefundIssued).ToList();
        issued.Select(e => e.Reference + "@" + e.Order.Id).Distinct().Should().HaveCount(12); // one credit-note input per refund
        issued.Should().HaveCount(12);
        Money.Sum(issued.Select(e => e.Refund!.Amount), "EUR").Should().Be(new Money(25m * sold, "EUR"));
        foreach (var order in orders)
            (await RefundHost.OrderAsync(Store, order.Id!)).Lines.All(l => l.Status == OrderLineStatus.Returned).Should().BeTrue();
    }

    private async Task<CheckoutMassRefund> MassAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<CheckoutMassRefund>(id))!;
    }
}
