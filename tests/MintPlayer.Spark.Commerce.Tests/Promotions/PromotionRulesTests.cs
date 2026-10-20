using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Promotions;
using MintPlayer.Spark.Commerce.Promotions.Contracts;
using MintPlayer.Spark.Commerce.Promotions.Models;
using MintPlayer.Spark.Commerce.Promotions.Services;

namespace MintPlayer.Spark.Commerce.Tests.Promotions;

/// <summary>Promotions' pure rules (M8): codes, the pro-rata spread, Omnibus, the waiting list's rule, back-in-stock, attribution.</summary>
public class PromotionRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static Money Eur(decimal amount) => new(amount, "EUR");

    [Theory]
    [InlineData(" summer-26 ", "SUMMER-26")]
    [InlineData("ab", null)]           // too short
    [InlineData("SUMMER 26", null)]    // a space inside
    [InlineData("promo|x", null)]      // '|' would break a wrapped item id
    [InlineData(null, null)]
    public void A_code_is_normalized_or_refused(string? typed, string? expected)
        => PromotionRules.NormalizeCode(typed).Should().Be(expected);

    [Fact]
    public void A_code_is_live_only_inside_its_window_and_while_active()
    {
        var code = new PromotionCode { Code = "X1X", ValidFrom = Now, ValidUntil = Now.AddDays(1) };
        PromotionRules.IsLive(code, Now.AddSeconds(-1)).Should().BeFalse();
        PromotionRules.IsLive(code, Now).Should().BeTrue();
        PromotionRules.IsLive(code, Now.AddDays(1)).Should().BeFalse(); // the end is exclusive
        code.Active = false;
        PromotionRules.IsLive(code, Now.AddHours(1)).Should().BeFalse();
    }

    [Fact]
    public void A_seller_code_applies_only_to_that_sellers_items_and_sees_through_wrapped_ids()
    {
        var code = new PromotionCode { Code = "SELLER", OrganizationId = "orgs/a", ItemIds = ["items/1"] };
        PromotionRules.Applies(code, "items/1", "orgs/a").Should().BeTrue();
        PromotionRules.Applies(code, "items/1", "orgs/b").Should().BeFalse();
        PromotionRules.Applies(code, "items/2", "orgs/a").Should().BeFalse();
        PromotionRules.Applies(code, PromotionItemIds.Blocked("WLABC", "items/1"), "orgs/a").Should().BeTrue();
        PromotionRules.Applies(new PromotionCode { Code = "ALL" }, "anything", null).Should().BeTrue();
    }

    [Fact]
    public void A_wrapped_item_id_round_trips_and_other_ids_do_not_parse()
    {
        var id = PromotionItemIds.Blocked("PARTNER", "products/12-A");
        PromotionItemIds.TryParse(id, out var code, out var inner).Should().BeTrue();
        code.Should().Be("PARTNER");
        inner.Should().Be("products/12-A");
        PromotionItemIds.TryParse("products/12-A", out _, out _).Should().BeFalse();
        PromotionItemIds.TryParse("promotions:|x", out _, out _).Should().BeFalse();
        PromotionItemIds.TryParse("promotions:CODE|", out _, out _).Should().BeFalse();
        PromotionItemIds.Inner(id).Should().Be("products/12-A");
    }

    [Fact]
    public void A_percentage_is_rounded_once_and_a_fixed_amount_never_exceeds_the_goods()
    {
        var percent = new PromotionCode { Code = "P15", Effect = PromotionEffect.PercentageOff, PercentBasisPoints = 1500 };
        PromotionRules.Discount(percent, Eur(33.33m)).Should().Be(Eur(5.00m)); // 4.9995 → 5.00, half away from zero (S21)

        var fixedOff = new PromotionCode { Code = "F10", Effect = PromotionEffect.FixedAmountOff, Amount = Eur(10m) };
        PromotionRules.Discount(fixedOff, Eur(25m)).Should().Be(Eur(10m));
        PromotionRules.Discount(fixedOff, Eur(7.5m)).Should().Be(Eur(7.5m));            // capped at the goods
        PromotionRules.Discount(fixedOff, new Money(25m, "USD")).Should().Be(new Money(0m, "USD")); // another currency: nothing
    }

    [Fact]
    public void The_spread_sums_exactly_to_the_discount_and_follows_the_line_amounts()
    {
        var parts = PromotionRules.SpreadProRata(Eur(10m), [Eur(10m), Eur(20m), Eur(30m)]);
        parts.Should().Equal(Eur(-1.67m), Eur(-3.33m), Eur(-5.00m));
        Money.Sum(parts, "EUR").Should().Be(Eur(-10m));

        // Largest remainder: 1 cent over three equal lines goes to the first (ties: earlier index).
        PromotionRules.SpreadProRata(Eur(0.01m), [Eur(1m), Eur(1m), Eur(1m)]).Should().Equal(Eur(-0.01m), Eur(0m), Eur(0m));
        // A zero line gets nothing.
        PromotionRules.SpreadProRata(Eur(5m), [Eur(0m), Eur(10m)]).Should().Equal(Eur(0m), Eur(-5m));
    }

    [Fact]
    public void A_line_discount_splits_per_unit_for_partial_refunds()
    {
        var perUnit = PromotionRules.PerUnit(Eur(-1.00m), 3);
        perUnit.Should().Equal(Eur(-0.34m), Eur(-0.33m), Eur(-0.33m));
        Money.Sum(perUnit, "EUR").Should().Be(Eur(-1.00m));
    }

    [Fact]
    public void The_discount_lands_on_eligible_lines_as_adjustments_and_respects_the_minimum()
    {
        var context = Context(Line("l1", "items/1", 20m, "orgs/a"), Line("l2", "items/2", 10m, "orgs/a"), Line("l3", "items/3", 50m, "orgs/b"));
        var code = new PromotionCode { Code = "SELLERA10", Effect = PromotionEffect.PercentageOff, PercentBasisPoints = 1000, OrganizationId = "orgs/a" };

        PromotionCodePricingStep.ApplyDiscount(context, code);

        context.Lines[0].Adjustments.Single().Amount.Should().Be(Eur(-2m));
        context.Lines[1].Adjustments.Single().Amount.Should().Be(Eur(-1m));
        context.Lines[2].Adjustments.Should().BeEmpty(); // another seller's line
        context.Lines[0].Adjustments.Single().Source.Should().Be("promotions");
        context.Total.Should().Be(Eur(77m));

        var minimum = new PromotionCode { Code = "MIN100", Effect = PromotionEffect.FixedAmountOff, Amount = Eur(5m), MinimumOrderTotal = Eur(100m) };
        var act = () => PromotionCodePricingStep.ApplyDiscount(Context(Line("l1", "items/1", 20m, null)), minimum);
        act.Should().Throw<CheckoutException>().Which.Code.Should().Be(PromotionRefusals.MinimumNotReached);

        var none = () => PromotionCodePricingStep.ApplyDiscount(Context(Line("l1", "items/9", 20m, "orgs/b")), code);
        none.Should().Throw<CheckoutException>().Which.Code.Should().Be(PromotionRefusals.CodeNotApplicable);
    }

    [Fact]
    public void A_progressive_reduction_keeps_the_start_of_its_first_step_where_the_member_state_adopted_art_6a5()
    {
        var first = new PromotionSalePrice { ValidFrom = Now, ValidUntil = Now.AddDays(7), SeriesStart = Now, SalePrice = Eur(80m) };
        PromotionRules.SeriesStart(first, Now.AddDays(7), Eur(60m), progressive: true).Should().Be(Now);              // follows on, lower again
        PromotionRules.SeriesStart(first, Now.AddDays(8), Eur(60m), progressive: true).Should().Be(Now.AddDays(8));   // a gap: a new reduction
        PromotionRules.SeriesStart(first, Now.AddDays(7), Eur(90m), progressive: true).Should().Be(Now.AddDays(7));   // higher: not a further step
        PromotionRules.SeriesStart(null, Now, Eur(60m), progressive: true).Should().Be(Now);
    }

    /// <summary>
    /// F5 (EUR-Lex check 2026-10-20): PID Art. 6a(5) is a member-state option ("Member States may provide that, when the
    /// price reduction is progressively increased, the prior price is the price without the price reduction before the
    /// first application"). Without it every step is a reduction of its own (Art. 6a(1)-(2)). Red before the fix: the
    /// series was kept everywhere.
    /// </summary>
    [Fact]
    public void Without_art_6a5_every_step_of_a_progressive_reduction_starts_its_own_reference()
    {
        var first = new PromotionSalePrice { ValidFrom = Now, ValidUntil = Now.AddDays(7), SeriesStart = Now, SalePrice = Eur(80m) };
        PromotionRules.SeriesStart(first, Now.AddDays(7), Eur(60m), progressive: false).Should().Be(Now.AddDays(7));
    }

    [Fact]
    public void The_progressive_rule_is_keyed_by_the_sellers_member_state_and_off_by_default()
    {
        new SalePriceOptions().ProgressiveReductionStates.Should().BeEmpty();
        string[] adopted = ["NL"];
        PromotionRules.ProgressiveReductionApplies("NL", "BE", adopted).Should().BeTrue();
        PromotionRules.ProgressiveReductionApplies("nl", "BE", adopted).Should().BeTrue();
        PromotionRules.ProgressiveReductionApplies("DE", "NL", adopted).Should().BeFalse();   // the seller's state, not the platform's
        PromotionRules.ProgressiveReductionApplies(null, "NL", adopted).Should().BeTrue();    // no seller country: the platform's
        PromotionRules.ProgressiveReductionApplies("NL", "BE", []).Should().BeFalse();
    }

    [Fact]
    public void The_announced_percentage_is_computed_on_the_prior_price_and_rounded_down()
    {
        PromotionRules.ReductionPercent(Eur(100m), Eur(80m)).Should().Be(20);
        PromotionRules.ReductionPercent(Eur(30m), Eur(20m)).Should().Be(33); // 33.3 → 33, never overstated
        PromotionRules.ReductionPercent(Eur(80m), Eur(80m)).HasValue.Should().BeFalse(); // not lower than the prior price: no reduction to announce
        PromotionRules.ReductionPercent(null, Eur(80m)).HasValue.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 0, true, true)]
    [InlineData(1, 0, true, false)]   // not sold out
    [InlineData(0, 2, true, false)]   // holds outstanding: they may still lapse back to the public
    [InlineData(0, 0, false, false)]  // the queue is still admitting
    public void The_waiting_list_activates_only_when_sold_out_queue_closed_and_no_holds(int available, int held, bool queueClosed, bool expected)
        => PromotionRules.ShouldActivate(available, held, queueClosed).Should().Be(expected);

    [Theory]
    [InlineData(5, 2, 500, 10)]
    [InlineData(3, 1.5, 500, 5)]  // ceil(4.5)
    [InlineData(1000, 2, 500, 500)]
    [InlineData(0, 2, 500, 0)]
    public void A_restock_mails_a_batch_of_restocked_times_factor(int restocked, double factor, int max, int expected)
        => PromotionRules.BackInStockBatch(restocked, (decimal)factor, max).Should().Be(expected);

    [Fact]
    public void The_first_attribution_counts_until_it_expires()
    {
        PromotionRules.ReplacesAttribution(null, Now).Should().BeTrue();
        var live = new PromotionAttribution { Ref = "A", ExpiresAt = Now.AddDays(1) };
        PromotionRules.ReplacesAttribution(live, Now).Should().BeFalse();
        PromotionRules.ReplacesAttribution(live, Now.AddDays(1)).Should().BeTrue();
    }

    [Fact]
    public void A_commission_is_a_percentage_plus_a_fixed_amount()
    {
        var link = new PromotionTrackingLink { Ref = "INFLUENCER", CommissionBasisPoints = 1000, CommissionFixed = Eur(0.50m) };
        PromotionRules.Commission(link, Eur(45.55m)).Should().Be(Eur(5.06m)); // 4.555 → 4.56, + 0.50
        PromotionRules.Commission(new PromotionTrackingLink { Ref = "NONE" }, Eur(45m)).Should().Be(Eur(0m));
    }

    [Fact]
    public void An_offer_code_is_a_valid_code()
    {
        var code = WaitingLists.NewOfferCode();
        code.Should().StartWith("WL").And.HaveLength(14);
        PromotionRules.NormalizeCode(code).Should().Be(code);
    }

    [Fact]
    public void Startup_refuses_settings_that_cannot_work()
    {
        var options = new SparkCommercePromotionsOptions();
        SparkCommercePromotionsExtensions.Problems(options).Should().BeEmpty();
        options.BackInStock.BatchFactor = 0;
        options.WaitingList.OfferValidity = TimeSpan.Zero;
        SparkCommercePromotionsExtensions.Problems(options).Should().HaveCount(2);
    }

    [Fact]
    public void A_code_request_is_validated_per_effect()
    {
        var block = new SparkPromotionCodeRequest("PARTNER", "orgs/a", "CapacityBlock", ItemIds: ["items/1"], BlockQuantity: 10);
        var noEnd = () => PromotionCodes.Validate(block, PromotionEffect.CapacityBlock);
        noEnd.Should().Throw<PromotionException>().Which.Subject.Should().Be("block"); // a block must end, to give its units back

        var ok = () => PromotionCodes.Validate(block with { ValidUntil = Now.AddDays(1) }, PromotionEffect.CapacityBlock);
        ok.Should().NotThrow();

        var percent = () => PromotionCodes.Validate(new("P", null, "PercentageOff", PercentBasisPoints: 12_000), PromotionEffect.PercentageOff);
        percent.Should().Throw<PromotionException>();
    }

    [Fact]
    public void A_token_matches_only_its_hash()
    {
        var token = PromotionHashes.NewToken();
        var hash = PromotionHashes.Token(token);
        PromotionHashes.Matches(token, hash).Should().BeTrue();
        PromotionHashes.Matches(token + "x", hash).Should().BeFalse();
        PromotionHashes.Matches(null, hash).Should().BeFalse();
    }

    // ---- helpers ----

    private static PricedLine Line(string lineId, string itemId, decimal gross, string? seller)
    {
        var amount = Eur(gross);
        return new PricedLine
        {
            LineId = lineId,
            Item = new CheckoutItem { ItemId = itemId, Channel = "shop", OrganizationId = seller, Price = amount, Title = TranslatedString.Of("en", itemId) },
            Quantity = 1,
            Price = new FinalPrice(amount, Eur(0m), amount, [], [], amount, amount),
        };
    }

    private static CheckoutPricingContext Context(params PricedLine[] lines) => new()
    {
        Currency = "EUR",
        Lines = lines,
        Choices = new Dictionary<string, string>(),
    };
}
