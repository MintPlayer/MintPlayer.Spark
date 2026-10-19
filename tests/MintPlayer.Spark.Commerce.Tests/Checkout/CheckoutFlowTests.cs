using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Services;
using MintPlayer.Spark.Commerce.Checkout.Jobs;
using MintPlayer.Spark.Commerce.Tests.Payments;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Commerce.Payments.Models;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using CheckoutContracts = MintPlayer.Spark.Commerce.Checkout.Contracts;

namespace MintPlayer.Spark.Commerce.Tests.Checkout;

/// <summary>
/// Checkout against RavenDB and the real inventory engine (M7): the three allocation modes (D1), channel purity and the
/// multi-channel basket (D40), mixed sellers (D41), terms acceptance (GA2), the age attestation (GB12), zero totals (D6),
/// race losers with <c>Basket:OnLineFailure</c>, late payments (D3), card-fingerprint limits (D29), the buyer's cancel,
/// disputes (GB1, I4), donations and insurance (GC3, GC4), and the visitor merge (X1).
/// </summary>
public class CheckoutFlowTests : SparkTestDriver
{
    private CheckoutHost Host(Action<SparkCommerceOptions>? commerce = null, Action<SparkCommerceCheckoutOptions>? checkout = null)
        => CheckoutHost.Build(Store, commerce, checkout);

    private async Task<CheckoutOrder> OrderAsync(string number)
    {
        using var session = Store.OpenAsyncSession();
        return await session.Query<CheckoutOrder>().Customize(c => c.WaitForNonStaleResults()).Where(o => o.Number == number).SingleAsync();
    }

    private static async Task<CheckoutException> Refused(Func<Task> action)
    {
        try { await action(); }
        catch (CheckoutException ex) { return ex; }
        throw new InvalidOperationException("Expected a checkout refusal.");
    }

    private static CheckoutContracts.SparkBasketAddRequest Add(string itemId, int quantity = 1) => new(itemId, quantity);

    // ---- HoldFromSelection --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_selection_hold_is_claimed_at_add_extended_at_pay_and_converted_on_payment()
    {
        var host = Host();
        await host.ItemAsync("tickets/a", "tickets", 25m);

        var basket = await host.Checkout.AddLineAsync(Add("tickets/a", 2));
        basket.Lines.Should().HaveCount(1);
        basket.EarliestHoldUntil.HasValue.Should().BeTrue();
        (await host.AvailableAsync("tickets/a")).Should().Be(8);

        var placed = await host.PlaceAsync();
        placed.State.Should().Be(nameof(OrderState.PaymentPending));
        placed.RedirectUrl.Should().StartWith("https://pay.example.org/");
        var creation = host.Payments.Created.Single();
        creation.AllowedMethods.Should().BeEquivalentTo(new[] { "card" }); // D3: bank transfer never in a held channel
        creation.ExpiresAt.Should().Be(host.Clock.Now + TimeSpan.FromMinutes(30));

        var order = await OrderAsync(placed.Number);
        order.Acceptances.Single().Version.Should().Be(1);
        (await host.Inventory.GetClaimAsync(order.Lines[0].ClaimId!))!.ExpiresAt.Should().Be(creation.ExpiresAt!.Value);

        await host.Lifecycle.OnPaymentAsync(host.Payments.Set(order.Payment!.PaymentId!, ProviderPaymentStatus.Paid), default);
        order = await OrderAsync(placed.Number);
        order.State.Should().Be(OrderState.Confirmed);
        (await host.Inventory.GetClaimAsync(order.Lines[0].ClaimId!))!.State.Should().Be(ClaimState.Converted);
        (await host.AvailableAsync("tickets/a")).Should().Be(8);
        host.Events.Events.Select(e => e.Kind).Should().BeEquivalentTo(new[] { OrderEventKind.Placed, OrderEventKind.Confirmed });
        (await host.Checkout.GetBasketAsync()).Lines.Should().BeEmpty();

        // A duplicate webhook changes nothing.
        await host.Lifecycle.OnPaymentAsync(host.Payments.Payments[order.Payment!.PaymentId!], default);
        host.Events.Events.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_buyer_cancelling_the_payment_hands_a_live_hold_back_to_the_basket()
    {
        var host = Host();
        await host.ItemAsync("tickets/c", "tickets", 10m);
        await host.Checkout.AddLineAsync(Add("tickets/c", 1));
        var placed = await host.PlaceAsync();

        var cancelled = await host.Checkout.CancelPaymentAsync(placed.Number);
        cancelled!.State.Should().Be(OrderState.Cancelled);
        host.Payments.Cancelled.Should().HaveCount(1); // D3: cancelled actively
        var claim = await host.Inventory.GetClaimAsync(cancelled.Lines[0].ClaimId!);
        claim!.State.Should().Be(ClaimState.Held);
        claim.ExpiresAt!.Value.Should().Be(cancelled.Lines[0].BasketHoldUntil!.Value);
        var basket = await host.Checkout.GetBasketAsync();
        basket.PendingOrderNumber.Should().BeNull();
        basket.Lines.Should().HaveCount(1);
    }

    // ---- channels, sellers ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_basket_is_channel_pure_unless_MixChannels_and_then_takes_the_shortest_window()
    {
        var pure = Host();
        await pure.ItemAsync("tickets/m", "tickets", 10m);
        await pure.ItemAsync("shop/m", "shop", 5m);
        await pure.Checkout.AddLineAsync(Add("tickets/m"));
        (await Refused(() => pure.Checkout.AddLineAsync(Add("shop/m")))).Code.Should().Be(CheckoutRefusals.ChannelMismatch);

        var mixed = Host(c => c.Basket.MixChannels = true);
        await mixed.ItemAsync("tickets/n", "tickets", 10m);
        await mixed.ItemAsync("box/n", "box", 8m);
        await mixed.ItemAsync("shop/n", "shop", 5m);
        await mixed.Checkout.AddLineAsync(Add("tickets/n"));
        await mixed.Checkout.AddLineAsync(Add("box/n"));
        await mixed.Checkout.AddLineAsync(Add("shop/n"));
        (await mixed.AvailableAsync("box/n")).Should().Be(10); // HoldFromCheckout: nothing until "Pay"

        var summary = await mixed.Checkout.GetSummaryAsync();
        summary.PaymentWindowMinutes.Should().Be(20); // the shortest held window (box), D40
        summary.OrderButtonKey.Should().Be("commerceCheckout.orderWithObligationToPay");

        await mixed.PlaceAsync();
        mixed.Payments.Created.Single().ExpiresAt.Should().Be(mixed.Clock.Now + TimeSpan.FromMinutes(20));
        (await mixed.AvailableAsync("box/n")).Should().Be(9);
        (await mixed.AvailableAsync("shop/n")).Should().Be(10); // OnPayment: soft check only
    }

    [Fact]
    public async Task Mixed_sellers_need_the_D41_opt_in_and_then_pay_with_a_split()
    {
        var refused = Host(c => c.Merchant = MerchantMode.ConnectedSellers);
        await refused.ItemAsync("tickets/s1", "tickets", 10m, seller: "Organizations/a");
        await refused.ItemAsync("tickets/s2", "tickets", 20m, seller: "Organizations/b");
        await refused.Checkout.AddLineAsync(Add("tickets/s1"));
        (await Refused(() => refused.Checkout.AddLineAsync(Add("tickets/s2")))).Code.Should().Be(CheckoutRefusals.SellerMismatch);

        var host = Host(c =>
        {
            c.Merchant = MerchantMode.ConnectedSellers;
            c.Basket.MixSellers = true;
            c.Basket.AcknowledgeSplitMerchantOfRecord = true;
        });
        await host.ItemAsync("tickets/s3", "tickets", 10m, seller: "Organizations/a");
        await host.ItemAsync("tickets/s4", "tickets", 20m, seller: "Organizations/b");
        await host.Checkout.AddLineAsync(Add("tickets/s3"));
        await host.Checkout.AddLineAsync(Add("tickets/s4"));
        await host.Checkout.PlaceOrderAsync(new CheckoutContracts.SparkPlaceOrderRequest(
            [host.Legal.TermsFor("Organizations/a").Id, host.Legal.TermsFor("Organizations/b").Id], Email: "buyer@example.org"));

        var creation = host.Payments.Created.Single();
        creation.SellerOrganizationId.Should().BeNull(); // the platform is merchant of record (D41)
        creation.Splits.Select(s => (s.SellerOrganizationId, s.Amount.Amount)).Should().BeEquivalentTo(new[] { ("Organizations/a", 10m), ("Organizations/b", 20m) });
    }

    // ---- the last step ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Terms_in_force_must_be_accepted_by_id_and_a_new_version_again()
    {
        var host = Host();
        await host.ItemAsync("tickets/t", "tickets", 10m);
        await host.Checkout.AddLineAsync(Add("tickets/t"));

        (await Refused(() => host.Checkout.PlaceOrderAsync(new CheckoutContracts.SparkPlaceOrderRequest([], Email: "b@example.org"))))
            .Code.Should().Be(CheckoutRefusals.TermsNotAccepted);
        var shown = host.Legal.TermsFor(null).Id;
        host.Legal.Version = 2;
        (await Refused(() => host.Checkout.PlaceOrderAsync(new CheckoutContracts.SparkPlaceOrderRequest([shown], Email: "b@example.org"))))
            .Code.Should().Be(CheckoutRefusals.TermsChanged);
        host.Legal.Missing = true;
        (await Refused(() => host.PlaceAsync())).Code.Should().Be(CheckoutRefusals.TermsMissing);
        host.Legal.Missing = false;

        var placed = await host.PlaceAsync();
        (await OrderAsync(placed.Number)).Acceptances.Single().Version.Should().Be(2);
    }

    [Fact]
    public async Task An_age_restricted_item_needs_the_attestation_and_marks_the_line_for_an_ID_check()
    {
        var host = Host();
        await host.ItemAsync("tickets/18", "tickets", 10m, minimumAge: 18);
        await host.Checkout.AddLineAsync(Add("tickets/18"));
        (await host.Checkout.GetSummaryAsync()).MinimumAge.Should().Be(18);

        (await Refused(() => host.PlaceAsync())).Code.Should().Be(CheckoutRefusals.AgeAttestationRequired);
        (await Refused(() => host.PlaceAsync(age: 16))).Code.Should().Be(CheckoutRefusals.AgeAttestationRequired);

        var order = await OrderAsync((await host.PlaceAsync(age: 18)).Number);
        order.AgeAttestation!.MinimumAge.Should().Be(18);
        order.Lines.Single().IdCheckRequired.Should().BeTrue();
    }

    [Fact]
    public async Task A_zero_total_order_confirms_without_a_provider()
    {
        var host = Host();
        await host.ItemAsync("tickets/free", "tickets", 0m);
        await host.Checkout.AddLineAsync(Add("tickets/free", 2));

        var placed = await host.PlaceAsync();
        placed.State.Should().Be(nameof(OrderState.Confirmed));
        host.Payments.Created.Should().BeEmpty();
        var order = await OrderAsync(placed.Number);
        (await host.Inventory.GetClaimAsync(order.Lines[0].ClaimId!))!.State.Should().Be(ClaimState.Converted);
    }

    [Fact]
    public async Task A_gated_item_is_refused_without_an_admission_gate()
    {
        var host = Host();
        await host.ItemAsync("tickets/gated", "tickets", 10m, admissionScope: "phases/presale");
        (await Refused(() => host.Checkout.AddLineAsync(new CheckoutContracts.SparkBasketAddRequest("tickets/gated", 1, AdmissionToken: "t"))))
            .Code.Should().Be(CheckoutRefusals.AdmissionRequired);
        (await host.AvailableAsync("tickets/gated")).Should().Be(10);
    }

    [Fact]
    public async Task An_add_on_needs_its_parent_line()
    {
        var host = Host();
        await host.ItemAsync("tickets/main", "tickets", 30m);
        await host.ItemAsync("tickets/parking", "tickets", 5m, parents: ["tickets/main"]);
        (await Refused(() => host.Checkout.AddLineAsync(Add("tickets/parking")))).Code.Should().Be(CheckoutRefusals.ParentRequired);

        var basket = await host.Checkout.AddLineAsync(Add("tickets/main"));
        basket = await host.Checkout.AddLineAsync(new CheckoutContracts.SparkBasketAddRequest("tickets/parking", 1, ParentLineId: basket.Lines[0].LineId));
        basket.Lines.Should().HaveCount(2);

        basket = await host.Checkout.SetQuantityAsync(basket.Lines[0].LineId, 0); // the add-on goes with its parent
        basket.Lines.Should().BeEmpty();
        (await host.AvailableAsync("tickets/parking")).Should().Be(10);
    }

    // ---- confirmation outcomes -------------------------------------------------------------------------------------

    [Fact]
    public async Task An_OnPayment_race_loser_is_refunded_alone_with_RefundLine()
    {
        var host = Host(c => c.Basket.MixChannels = true);
        await host.ItemAsync("tickets/r", "tickets", 30m);
        await host.ItemAsync("shop/r", "shop", 12m, capacity: 1);
        await host.Checkout.AddLineAsync(Add("tickets/r"));
        await host.Checkout.AddLineAsync(Add("shop/r"));
        var placed = await host.PlaceAsync();

        // Someone else buys the last shirt before this payment lands.
        (await host.Inventory.TryClaimAsync(new ClaimRequest("claims/other", "baskets/other", host.Clock.Now.AddMinutes(10), [], Items: [new ItemLine("shop/r", 1)]))).Succeeded.Should().BeTrue();

        var order = await OrderAsync(placed.Number);
        await host.Lifecycle.OnPaymentAsync(host.Payments.Set(order.Payment!.PaymentId!, ProviderPaymentStatus.Paid), default);
        order = await OrderAsync(placed.Number);
        order.State.Should().Be(OrderState.Confirmed);
        order.Lines.Single(l => l.ItemId == "shop/r").Status.Should().Be(OrderLineStatus.Refused);
        host.Payments.Refunds.Single().Amount.Should().Be(new Money(12m, "EUR"));
        host.Events.Events.Should().Contain(e => e.Kind == OrderEventKind.LineRefused);
    }

    [Fact]
    public async Task VoidOrder_gives_everything_back_when_a_line_loses()
    {
        var host = Host(c =>
        {
            c.Basket.MixChannels = true;
            c.Basket.OnLineFailure = BasketLineFailurePolicy.VoidOrder;
        });
        await host.ItemAsync("tickets/v", "tickets", 30m);
        await host.ItemAsync("shop/v", "shop", 12m, capacity: 1);
        await host.Checkout.AddLineAsync(Add("tickets/v"));
        await host.Checkout.AddLineAsync(Add("shop/v"));
        var placed = await host.PlaceAsync();
        (await host.Inventory.TryClaimAsync(new ClaimRequest("claims/other-v", "baskets/other", host.Clock.Now.AddMinutes(10), [], Items: [new ItemLine("shop/v", 1)]))).Succeeded.Should().BeTrue();

        var order = await OrderAsync(placed.Number);
        await host.Lifecycle.OnPaymentAsync(host.Payments.Set(order.Payment!.PaymentId!, ProviderPaymentStatus.Paid), default);
        order = await OrderAsync(placed.Number);
        order.State.Should().Be(OrderState.Refunding);
        host.Payments.Refunds.Single().Amount.Should().Be(new Money(42m, "EUR"));
        (await host.AvailableAsync("tickets/v")).Should().Be(10);
    }

    [Fact]
    public async Task A_late_payment_reclaims_the_same_units_else_is_refunded()
    {
        var host = Host();
        await host.ItemAsync("tickets/late", "tickets", 20m, capacity: 1);
        await host.Checkout.AddLineAsync(Add("tickets/late"));
        var placed = await host.PlaceAsync();
        var order = await OrderAsync(placed.Number);

        // The window closes: the sweep expires the order and releases the hold.
        host.Clock.Advance(TimeSpan.FromMinutes(40));
        await CheckoutSweepJob.ReconcileAsync(order.Id!, host.Lifecycle, host.Payments, default);
        (await OrderAsync(placed.Number)).State.Should().Be(OrderState.Expired);
        (await host.AvailableAsync("tickets/late")).Should().Be(1);

        // The payment lands anyway: the unit is still free, so it is re-claimed and the order confirmed (D3).
        await host.Lifecycle.OnPaymentAsync(host.Payments.Set(order.Payment!.PaymentId!, ProviderPaymentStatus.Paid), default);
        (await OrderAsync(placed.Number)).State.Should().Be(OrderState.Confirmed);
        (await host.AvailableAsync("tickets/late")).Should().Be(0);
    }

    [Fact]
    public async Task A_late_payment_whose_units_are_gone_is_refunded()
    {
        var host = Host();
        await host.ItemAsync("tickets/gone", "tickets", 20m, capacity: 1);
        await host.Checkout.AddLineAsync(Add("tickets/gone"));
        var placed = await host.PlaceAsync();
        var order = await OrderAsync(placed.Number);
        host.Clock.Advance(TimeSpan.FromMinutes(40));
        await CheckoutSweepJob.ReconcileAsync(order.Id!, host.Lifecycle, host.Payments, default);
        (await host.Inventory.TryClaimAsync(new ClaimRequest("claims/next", "baskets/next", host.Clock.Now.AddMinutes(10), [], Items: [new ItemLine("tickets/gone", 1)]))).Succeeded.Should().BeTrue();

        await host.Lifecycle.OnPaymentAsync(host.Payments.Set(order.Payment!.PaymentId!, ProviderPaymentStatus.Paid), default);
        (await OrderAsync(placed.Number)).State.Should().Be(OrderState.Refunding);
        host.Payments.Refunds.Single().Amount.Should().Be(new Money(20m, "EUR"));
    }

    [Fact]
    public async Task A_card_fingerprint_over_the_per_buyer_limit_refuses_the_order()
    {
        var host = Host();
        await host.ItemAsync("tickets/fp", "tickets", 10m, perBuyerLimit: 2);

        host.Buyer.UserId = null;
        host.Buyer.VisitorId = "visitor-a";
        await host.Checkout.AddLineAsync(Add("tickets/fp", 2));
        var first = await OrderAsync((await host.PlaceAsync(email: "a@example.org")).Number);
        await host.Lifecycle.OnPaymentAsync(host.Payments.Set(first.Payment!.PaymentId!, ProviderPaymentStatus.Paid, fingerprint: "fp-1"), default);
        (await OrderAsync(first.Number)).State.Should().Be(OrderState.Confirmed);

        // Another identity, the same card (D29): Mollie reports the fingerprint after payment, so it is refunded.
        host.Buyer.VisitorId = "visitor-b";
        await host.Checkout.AddLineAsync(Add("tickets/fp", 1));
        var second = await OrderAsync((await host.PlaceAsync(email: "b@example.org")).Number);
        await host.Lifecycle.OnPaymentAsync(host.Payments.Set(second.Payment!.PaymentId!, ProviderPaymentStatus.Paid, fingerprint: "fp-1"), default);
        (await OrderAsync(second.Number)).State.Should().Be(OrderState.Refunding);
        (await host.AvailableAsync("tickets/fp")).Should().Be(8);
    }

    [Fact]
    public async Task A_second_payment_for_a_paid_order_is_refunded()
    {
        var host = Host();
        await host.ItemAsync("tickets/dup", "tickets", 10m);
        await host.Checkout.AddLineAsync(Add("tickets/dup"));
        var order = await OrderAsync((await host.PlaceAsync()).Number);
        await host.Lifecycle.OnPaymentAsync(host.Payments.Set(order.Payment!.PaymentId!, ProviderPaymentStatus.Paid), default);

        // A second provider payment for the same order (a second tab) lands.
        var second = await host.Payments.CreatePaymentAsync(new PaymentCreation { Reference = order.Id!, Attempt = 1, Amount = order.Total, Description = "x", RedirectUrl = "/" });
        await host.Lifecycle.UpdateAsync(order.Id!, (_, o, _) =>
        {
            o.Payments.Add(new CheckoutOrderPayment { PaymentId = second.Id, Amount = o.Total, Attempt = 1 });
            return Task.CompletedTask;
        }, default);
        await host.Lifecycle.OnPaymentAsync(host.Payments.Set(second.Id!, ProviderPaymentStatus.Paid), default);
        host.Payments.Refunds.Single().Reference.Should().Contain("superfluous");
        (await OrderAsync(order.Number)).State.Should().Be(OrderState.Confirmed);
    }

    // ---- disputes (GB1) ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_lost_dispute_returns_the_units_once_and_a_won_one_returns_nothing()
    {
        var host = Host();
        await host.ItemAsync("tickets/d", "tickets", 10m);
        await host.Checkout.AddLineAsync(Add("tickets/d", 3));
        var order = await OrderAsync((await host.PlaceAsync()).Number);
        await host.Lifecycle.OnPaymentAsync(host.Payments.Set(order.Payment!.PaymentId!, ProviderPaymentStatus.Paid), default);

        var dispute = new PaymentDispute { Id = "PaymentDisputes/1", Reference = order.Id, Amount = order.Total, Status = DisputeStatus.NeedsResponse };
        await host.Lifecycle.OnDisputeAsync(new DisputeChangeNotice(dispute, null), default);
        (await OrderAsync(order.Number)).State.Should().Be(OrderState.Disputed);

        dispute.Status = DisputeStatus.Lost;
        await host.Lifecycle.OnDisputeAsync(new DisputeChangeNotice(dispute, DisputeStatus.NeedsResponse), default);
        await host.Lifecycle.OnDisputeAsync(new DisputeChangeNotice(dispute, DisputeStatus.NeedsResponse), default); // redelivered
        (await OrderAsync(order.Number)).State.Should().Be(OrderState.DisputeLost);
        (await host.AvailableAsync("tickets/d")).Should().Be(10); // returned exactly once (I4)

        var won = Host();
        await won.ItemAsync("tickets/w", "tickets", 10m);
        await won.Checkout.AddLineAsync(Add("tickets/w"));
        var wonOrder = await OrderAsync((await won.PlaceAsync()).Number);
        await won.Lifecycle.OnPaymentAsync(won.Payments.Set(wonOrder.Payment!.PaymentId!, ProviderPaymentStatus.Paid), default);
        var wonDispute = new PaymentDispute { Id = "PaymentDisputes/2", Reference = wonOrder.Id, Amount = wonOrder.Total, Status = DisputeStatus.NeedsResponse };
        await won.Lifecycle.OnDisputeAsync(new DisputeChangeNotice(wonDispute, null), default);
        wonDispute.Status = DisputeStatus.Won;
        await won.Lifecycle.OnDisputeAsync(new DisputeChangeNotice(wonDispute, DisputeStatus.NeedsResponse), default);
        (await OrderAsync(wonOrder.Number)).State.Should().Be(OrderState.DisputeWon);
        (await won.AvailableAsync("tickets/w")).Should().Be(9);
    }

    // ---- donations and insurance (GC3, GC4) -------------------------------------------------------------------------

    [Fact]
    public async Task Insurance_is_offered_unticked_and_bound_once_chosen_and_a_round_up_donation_is_an_extra()
    {
        var host = Host(checkout: o =>
        {
            o.Donation.Enabled = true;
            o.Donation.Beneficiary = "Food bank";
        });
        await host.ItemAsync("tickets/i", "tickets", 24.30m);
        await host.Checkout.AddLineAsync(Add("tickets/i"));

        var summary = await host.Checkout.GetSummaryAsync();
        summary.Insurance.Should().NotBeNull();
        summary.Extras.Should().BeEmpty(); // nothing pre-ticked
        summary.Donation!.RoundUp.Should().BeTrue();

        await host.Checkout.SetChoicesAsync(new Dictionary<string, string?> { ["donation"] = "roundup", ["insurance"] = "accept" }, null);
        summary = await host.Checkout.GetSummaryAsync();
        summary.Extras.Select(e => e.Code).Should().BeEquivalentTo(new[] { "donation", "insurance" });
        summary.Extras.Single(e => e.Code == "donation").Amount.Should().Be(new Money(0.70m, "EUR"));

        var order = await OrderAsync((await host.PlaceAsync()).Number);
        await host.Lifecycle.OnPaymentAsync(host.Payments.Set(order.Payment!.PaymentId!, ProviderPaymentStatus.Paid), default);
        (await OrderAsync(order.Number)).InsurancePolicyId.Should().StartWith("test-policy-");
    }

    // ---- the visitor merge (X1) -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_guest_basket_moves_onto_the_account_at_sign_in()
    {
        var host = Host();
        await host.ItemAsync("tickets/g", "tickets", 10m);
        host.Buyer.VisitorId = "visitor-merge";
        await host.Checkout.AddLineAsync(Add("tickets/g", 2));

        using (var session = Store.OpenAsyncSession(new SessionOptions { TransactionMode = TransactionMode.ClusterWide }))
        {
            await new CheckoutBasketMerge(new StaticOptionsMonitor<SparkCommerceOptions>(host.Commerce), host.Clock)
                .MergeAsync("visitor-merge", "users/1", session, default);
            await session.SaveChangesAsync();
        }

        host.Buyer.UserId = "users/1";
        var basket = await host.Checkout.GetBasketAsync();
        basket.Lines.Single().Quantity.Should().Be(2);
        host.Buyer.UserId = null;
        (await host.Checkout.GetBasketAsync()).Lines.Should().BeEmpty();
    }
}
