using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Services;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Pricing;
using MintPlayer.Spark.Commerce.Tests.Payments;

namespace MintPlayer.Spark.Commerce.Tests.Checkout;

/// <summary>
/// Item 1 (bug): an order line's net and VAT come from the adjusted total (S21), not from the price before a discount.
/// Red before the fix: <c>ToOrderLine</c> copied <c>FinalPrice.Net/Vat</c> of the undiscounted price (10.00 / 2.10).
/// </summary>
public class CheckoutLineVatTests
{
    /// <summary>A step that discounts every line by a fixed amount (what Promotions does through its own step).</summary>
    private sealed class Discount(decimal amount) : ICheckoutPricingStep
    {
        public int Order => 100;

        public Task ApplyAsync(CheckoutPricingContext context, CancellationToken cancellationToken)
        {
            foreach (var line in context.Lines)
                line.Adjustments.Add(new PriceAdjustment("promo", new Money(-amount, context.Currency), "promo", "test"));
            return Task.CompletedTask;
        }
    }

    private static (CheckoutPricing Pricing, CommerceChannelOptions Channel) Pricing(CheckoutVatRounding rounding, params ICheckoutPricingStep[] steps)
    {
        var commerce = new SparkCommerceOptions();
        var channel = new CommerceChannelOptions
        {
            Fees = [new FeeRuleOptions { Code = "service", Kind = FeeKind.Service, Scope = FeeScope.PerUnit, Fixed = new() { ["EUR"] = 1m } }],
        };
        commerce.Channels["tickets"] = channel;
        var monitor = new StaticOptionsMonitor<SparkCommerceOptions>(commerce);
        var checkout = new StaticOptionsMonitor<SparkCommerceCheckoutOptions>(new SparkCommerceCheckoutOptions { VatRounding = rounding });
        return (new CheckoutPricing(new FinalPriceCalculator(monitor), monitor, steps, checkout), channel);
    }

    private static CheckoutItem Item(string id, decimal price, int? rate = 2100, PriceBasis basis = PriceBasis.Gross) => new()
    {
        ItemId = id, Channel = "tickets", Price = new Money(price, "EUR"), Title = TranslatedString.Of("en", id),
        VatRateBasisPoints = rate, PriceBasis = basis,
    };

    [Fact]
    public async Task A_discounted_order_line_carries_net_and_VAT_of_what_the_buyer_pays()
    {
        var (pricing, channel) = Pricing(CheckoutVatRounding.PerLine, new Discount(2.10m));
        var item = Item("x", 12.10m);
        var priced = await pricing.PriceAsync([new PricingInput("a", item, 1)], new Dictionary<string, string>(), null, true, default);

        var line = CheckoutService.ToOrderLine(priced.Lines[0], item, channel, null, new BuyerIdentifiers());

        // Paid for the goods: 12.10 − 2.10 = 10.00 (the 1.00 service fee is not goods). VAT 21 % contained: RoundDiv(1000 × 2100, 12100) = 174.
        line.Total.Should().Be(new Money(11.00m, "EUR"));
        line.Vat.Should().Be(new Money(1.74m, "EUR"));
        line.Net.Should().Be(new Money(8.26m, "EUR"));
        (line.Net + line.Vat).Should().Be(line.Total - line.Fees.Select(f => f.Amount).Aggregate((a, b) => a + b));
    }

    [Fact]
    public async Task An_undiscounted_line_keeps_the_calculators_split()
    {
        var (pricing, _) = Pricing(CheckoutVatRounding.PerLine);
        var priced = await pricing.PriceAsync([new PricingInput("a", Item("x", 12.10m), 3)], new Dictionary<string, string>(), null, false, default);
        priced.Lines[0].Net.Should().Be(priced.Lines[0].Price.Net);
        priced.Lines[0].Vat.Should().Be(priced.Lines[0].Price.Vat);
    }

    [Fact]
    public async Task A_net_priced_discounted_line_picks_the_net_whose_VAT_adds_up_exactly()
    {
        var (pricing, _) = Pricing(CheckoutVatRounding.PerLine, new Discount(1.00m));
        var priced = await pricing.PriceAsync([new PricingInput("a", Item("x", 10.00m, basis: PriceBasis.Net), 1)], new Dictionary<string, string>(), null, false, default);
        var line = priced.Lines[0];
        // Goods paid: 12.10 − 1.00 = 11.10; net 9.17 + round(9.17 × 21 %) 1.93 = 11.10 (BR-CO-17 exact).
        line.Net.Should().Be(new Money(9.17m, "EUR"));
        line.Vat.Should().Be(new Money(1.93m, "EUR"));
    }

    [Fact]
    public async Task Per_group_rounding_rounds_once_per_rate_and_lines_sum_to_the_group()
    {
        var (pricing, _) = Pricing(CheckoutVatRounding.PerGroup, new Discount(0.33m));
        var priced = await pricing.PriceAsync(
            [new PricingInput("a", Item("x", 3.33m), 1), new PricingInput("b", Item("y", 3.33m), 1), new PricingInput("c", Item("z", 3.34m), 1)],
            new Dictionary<string, string>(), null, false, default);

        var goods = priced.Lines.Sum(CheckoutLineVat.Goods);
        var groupVat = Money.RoundDiv(goods * 2100, 12_100);
        priced.Lines.Sum(l => l.Vat!.ToMinorUnits()).Should().Be(groupVat);
        foreach (var line in priced.Lines)
            (line.Net!.ToMinorUnits() + line.Vat!.ToMinorUnits()).Should().Be(CheckoutLineVat.Goods(line));
    }

    [Fact]
    public async Task A_line_without_a_rate_has_no_VAT_even_when_discounted()
    {
        var (pricing, _) = Pricing(CheckoutVatRounding.PerLine, new Discount(1m));
        var priced = await pricing.PriceAsync([new PricingInput("a", Item("x", 5m, rate: null), 1)], new Dictionary<string, string>(), null, false, default);
        priced.Lines[0].Vat.Should().Be(Money.Zero("EUR"));
        priced.Lines[0].Net.Should().Be(new Money(4m, "EUR"));
    }
}
