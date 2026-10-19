using System.Reflection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Jobs;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Services;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Commerce.Pricing;
using MintPlayer.Spark.Commerce.Tests.Payments;
using CheckoutContracts = MintPlayer.Spark.Commerce.Checkout.Contracts;

namespace MintPlayer.Spark.Commerce.Tests.Checkout;

/// <summary>Checkout's pure rules (M7): the state machine, pricing, the merge rule, the reminder rule, evidence, GA11.</summary>
public class CheckoutRulesTests
{
    [Theory]
    [InlineData(OrderState.PaymentPending, OrderState.Confirmed, true)]
    [InlineData(OrderState.Expired, OrderState.Confirmed, true)]      // late payment re-claimed (D3)
    [InlineData(OrderState.Confirmed, OrderState.Disputed, true)]
    [InlineData(OrderState.Disputed, OrderState.DisputeLost, true)]
    [InlineData(OrderState.Refunded, OrderState.Confirmed, false)]    // a late webhook never resurrects a refunded order
    [InlineData(OrderState.Voided, OrderState.Confirmed, false)]
    [InlineData(OrderState.DisputeLost, OrderState.DisputeWon, false)]
    [InlineData(OrderState.Confirmed, OrderState.PaymentPending, false)]
    public void The_state_machine_allows_only_the_transitions_of_section_4_4(OrderState from, OrderState to, bool allowed)
        => OrderStateMachine.CanMove(from, to).Should().Be(allowed);

    [Fact]
    public void A_move_is_logged_and_an_illegal_one_throws()
    {
        var order = new CheckoutOrder { Id = "CheckoutOrders/1", State = OrderState.PaymentPending };
        OrderStateMachine.Move(order, OrderState.Confirmed, DateTimeOffset.UnixEpoch).Should().Be(OrderState.PaymentPending);
        order.Log.Single().Event.Should().Be("state:Confirmed");
        Action illegal = () => OrderStateMachine.Move(order, OrderState.PaymentPending, DateTimeOffset.UnixEpoch);
        illegal.Should().Throw<InvalidOperationException>();
    }

    private static (CheckoutPricing Pricing, SparkCommerceOptions Commerce, SparkCommerceCheckoutOptions Checkout) Pricing(params ICheckoutPricingStep[] extra)
    {
        var commerce = new SparkCommerceOptions();
        commerce.Channels["tickets"] = new CommerceChannelOptions
        {
            Fees =
            [
                new FeeRuleOptions { Code = "service", Kind = FeeKind.Service, Scope = FeeScope.PerUnit, Fixed = new() { ["EUR"] = 1m } },
                new FeeRuleOptions { Code = "booking", Kind = FeeKind.Booking, Scope = FeeScope.PerOrder, Fixed = new() { ["EUR"] = 2m } },
            ],
        };
        var checkout = new SparkCommerceCheckoutOptions();
        var monitor = new StaticOptionsMonitor<SparkCommerceOptions>(commerce);
        var steps = new List<ICheckoutPricingStep> { new DonationPricingStep(new StaticOptionsMonitor<SparkCommerceCheckoutOptions>(checkout)) };
        steps.AddRange(extra);
        return (new CheckoutPricing(new FinalPriceCalculator(monitor), monitor, steps), commerce, checkout);
    }

    private static CheckoutItem Item(string id, decimal price) => new()
    {
        ItemId = id, Channel = "tickets", Price = new Money(price, "EUR"), Title = TranslatedString.Of("en", id),
    };

    [Fact]
    public async Task FinalPrice_counts_a_channels_per_order_fee_once_per_order()
    {
        var (pricing, _, _) = Pricing();
        var priced = await pricing.PriceAsync([new PricingInput("a", Item("x", 10m), 2), new PricingInput("b", Item("y", 5m), 1)],
            new Dictionary<string, string>(), null, false, default);
        // 2×(10+1) + 1×(5+1) + one booking fee of 2.
        priced.Total.Should().Be(new Money(30m, "EUR"));
        priced.Lines[1].Price.MandatoryFees.Should().NotContain(f => f.Code == "booking");
    }

    [Fact]
    public async Task A_donation_is_a_configured_amount_or_a_round_up_and_nothing_else()
    {
        var (pricing, _, checkout) = Pricing();
        checkout.Donation.Enabled = true;
        checkout.Donation.FixedAmounts["EUR"] = [2m, 5m];
        var lines = new[] { new PricingInput("a", Item("x", 7.40m), 1) };

        var none = await pricing.PriceAsync(lines, new Dictionary<string, string>(), null, false, default);
        none.Extras.Should().BeEmpty(); // never pre-selected

        var fixedAmount = await pricing.PriceAsync(lines, new Dictionary<string, string> { ["donation"] = "5" }, null, false, default);
        fixedAmount.Extras.Single().Amount.Should().Be(new Money(5m, "EUR"));
        fixedAmount.Extras.Single().VatCategory.Should().Be("outside-scope");

        var roundUp = await pricing.PriceAsync(lines, new Dictionary<string, string> { ["donation"] = "roundup" }, null, false, default);
        roundUp.Total.Should().Be(new Money(11m, "EUR")); // 7.40 + 1 (service) + 2 (booking) = 10.40, rounded up to 11
        roundUp.Extras.Single().Amount.Should().Be(new Money(0.60m, "EUR"));
    }

    [Fact]
    public async Task An_unconfigured_donation_amount_is_refused()
    {
        var (pricing, _, checkout) = Pricing();
        checkout.Donation.Enabled = true;
        checkout.Donation.FixedAmounts["EUR"] = [2m];
        CheckoutException? refused = null;
        try { await pricing.PriceAsync([new PricingInput("a", Item("x", 1m), 1)], new Dictionary<string, string> { ["donation"] = "3" }, null, false, default); }
        catch (CheckoutException ex) { refused = ex; }
        refused!.Code.Should().Be(CheckoutRefusals.DonationInvalid);
    }

    [Fact]
    public void The_visitor_merge_appends_compatible_lines_else_the_newer_basket_wins()
    {
        var settings = new SparkCommerceOptions();
        var account = new CheckoutBasket { UpdatedAt = DateTimeOffset.UnixEpoch, Lines = [new CheckoutBasketLine { LineId = "1", Channel = "tickets" }] };
        var guest = new CheckoutBasket { UpdatedAt = DateTimeOffset.UnixEpoch.AddHours(1), Lines = [new CheckoutBasketLine { LineId = "2", Channel = "tickets" }] };
        CheckoutBasketMerge.Merge(guest, account, settings);
        account.Lines.Select(l => l.LineId).Should().BeEquivalentTo(new[] { "1", "2" });

        var other = new CheckoutBasket { UpdatedAt = DateTimeOffset.UnixEpoch.AddHours(2), Lines = [new CheckoutBasketLine { LineId = "3", Channel = "shop" }] };
        CheckoutBasketMerge.Merge(other, account, settings); // MixChannels off: the newer guest basket wins
        account.Lines.Select(l => l.LineId).Should().BeEquivalentTo(new[] { "3" });
    }

    [Fact]
    public void The_abandoned_basket_reminder_needs_an_opted_in_channel_and_never_fires_for_held_seats()
    {
        var plain = new CheckoutBasket { Lines = [new CheckoutBasketLine { Channel = "shop" }] };
        AbandonedBasketJob.Eligible(plain, ["shop"]).Should().BeTrue();
        AbandonedBasketJob.Eligible(plain, ["tickets"]).Should().BeFalse();
        var seats = new CheckoutBasket { Lines = [new CheckoutBasketLine { Channel = "shop", HeldUnits = ["A1"] }] };
        AbandonedBasketJob.Eligible(seats, ["shop"]).Should().BeFalse();
    }

    [Fact]
    public void The_evidence_section_states_the_accepted_terms_and_the_purchase_facts()
    {
        var order = new CheckoutOrder
        {
            Id = "CheckoutOrders/1", Number = "ABC", BuyerEmail = "b@example.org", PurchaseIp = "203.0.113.7", Total = new Money(10m, "EUR"),
            CreatedAt = DateTimeOffset.UnixEpoch,
            Lines = [new CheckoutOrderLine { ItemId = "tickets/a", Quantity = 1, Title = TranslatedString.Of("en", "Concert"), ServiceDate = DateTimeOffset.Parse("2026-12-01T20:00:00Z") }],
            Acceptances = [new CheckoutOrderAcceptance { DocumentId = "LegalDocuments/1", Kind = LegalDocumentKinds.TermsOfSale, Version = 3, AcceptedAt = DateTimeOffset.UnixEpoch }],
        };
        var section = CheckoutDisputeEvidence.Build(order);
        section.Fields![DisputeEvidenceField.CustomerPurchaseIp].Should().Be("203.0.113.7");
        section.Fields[DisputeEvidenceField.ServiceDate].Should().Be("2026-12-01");
        section.Fields[DisputeEvidenceField.RefundPolicyDisclosure].Should().Contain("v3");
        section.Lines.Should().Contain(l => l.Contains("terms-of-sale v3"));
    }

    [Fact]
    public void Nothing_a_buyer_sends_to_checkout_carries_a_country_GA11()
    {
        // Regulation (EU) 2018/302 Art. 3–5: no refusal or different conditions by nationality, residence or the card's
        // issuing country. Checkout takes no such input, so no rule in it can key on one (delivery zones live in Shipping).
        var forbidden = new[] { "country", "nationality", "residence", "issuer" };
        var requests = typeof(CheckoutContracts.SparkPlaceOrderRequest).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(CheckoutContracts.SparkPlaceOrderRequest).Namespace && t.Name.EndsWith("Request", StringComparison.Ordinal));
        foreach (var type in requests)
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => forbidden.Any(f => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                .Should().BeEmpty();
        typeof(PaymentMethodQuery).GetProperties().Should().NotContain(p => p.Name.Contains("Country", StringComparison.OrdinalIgnoreCase));
    }
}
