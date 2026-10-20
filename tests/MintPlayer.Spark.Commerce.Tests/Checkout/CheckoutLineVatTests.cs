using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
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

    /// <summary>An order-level discount (what Promotions adds for a basket-wide code): an extra, not a line adjustment.</summary>
    private sealed class OrderDiscount(decimal amount) : ICheckoutPricingStep
    {
        public int Order => 100;

        public Task ApplyAsync(CheckoutPricingContext context, CancellationToken cancellationToken)
        {
            context.Extras.Add(new PricedExtra("basket10", PricedExtraKind.Discount, new Money(-amount, context.Currency), "promo", "test", null, null));
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

    /// <summary>
    /// Red before the fix: an order-level discount (a <see cref="PricedExtraKind.Discount"/> extra) never reached the
    /// lines, so each line kept the VAT of its undiscounted 12.10 (2.10) while the buyer paid 2.42 less in total.
    /// </summary>
    [Theory]
    [InlineData(CheckoutVatRounding.PerLine)]
    [InlineData(CheckoutVatRounding.PerGroup)]
    public async Task An_order_discount_lowers_each_lines_VAT_base_pro_rata(CheckoutVatRounding rounding)
    {
        var (pricing, _) = Pricing(rounding, new OrderDiscount(2.42m));
        var priced = await pricing.PriceAsync(
            [new PricingInput("a", Item("x", 12.10m), 1), new PricingInput("b", Item("y", 12.10m), 1)],
            new Dictionary<string, string>(), null, false, default);

        // Goods 12.10 + 12.10; the 2.42 discount splits 1.21 / 1.21 (largest remainder); each base 10.89 contains
        // RoundDiv(1089 × 2100, 12100) = 1.89 VAT. Before the fix: 2.10 / 10.00 per line.
        foreach (var line in priced.Lines)
        {
            line.OrderDiscount.Should().Be(new Money(-1.21m, "EUR"));
            line.Vat.Should().Be(new Money(1.89m, "EUR"));
            line.Net.Should().Be(new Money(9.00m, "EUR"));
        }
    }

    [Fact]
    public async Task An_uneven_order_discount_splits_by_largest_remainder_and_the_bases_sum_to_what_is_paid()
    {
        var (pricing, _) = Pricing(CheckoutVatRounding.PerLine, new OrderDiscount(1.00m));
        var priced = await pricing.PriceAsync(
            [new PricingInput("a", Item("x", 3.33m), 1), new PricingInput("b", Item("y", 3.33m), 1), new PricingInput("c", Item("z", 3.34m), 1)],
            new Dictionary<string, string>(), null, false, default);

        // Money.Allocate(100, [333, 333, 334]) = 33 / 33 / 34: every base becomes 3.00, VAT RoundDiv(300 × 2100, 12100) = 52.
        priced.Lines.Select(l => l.OrderDiscount!.ToMinorUnits()).Should().Equal(-33L, -33L, -34L);
        foreach (var line in priced.Lines)
        {
            line.Vat.Should().Be(new Money(0.52m, "EUR"));
            line.Net.Should().Be(new Money(2.48m, "EUR"));
        }
        var bases = priced.Lines.Sum(l => l.Net!.ToMinorUnits() + l.Vat!.ToMinorUnits());
        bases.Should().Be(priced.Lines.Sum(CheckoutLineVat.Goods) - 100);
    }

    /// <summary>
    /// Red before the fix: the order line dropped the share, so its Net + Vat (10.89) no longer equalled Total − fees
    /// (12.10) and refunds and credit notes reading the line used the undiscounted amount.
    /// </summary>
    [Fact]
    public async Task The_order_line_stores_its_order_discount_share_and_its_amounts_add_up()
    {
        var (pricing, channel) = Pricing(CheckoutVatRounding.PerLine, new OrderDiscount(2.42m));
        var items = new[] { Item("x", 12.10m), Item("y", 12.10m) };
        var priced = await pricing.PriceAsync(
            [new PricingInput("a", items[0], 1), new PricingInput("b", items[1], 1)],
            new Dictionary<string, string>(), null, true, default);

        var lines = priced.Lines.Select((p, i) => CheckoutService.ToOrderLine(p, items[i], channel, null, new BuyerIdentifiers())).ToList();
        foreach (var line in lines)
        {
            line.OrderDiscount.Should().Be(new Money(-1.21m, "EUR"));
            (line.Net + line.Vat).Should().Be(line.ChargedGoods());
            line.ChargedGoods().Should().Be(new Money(10.89m, "EUR"));
            line.Charged().Should().Be(line.Total + line.OrderDiscount);
        }
        // What the buyer pays = every line as charged (the discount extra is exactly the lines' shares).
        Money.Sum(lines.Select(l => l.Charged()), "EUR").Should().Be(priced.Total);
    }

    [Fact]
    public async Task A_net_priced_line_with_an_order_discount_stays_BR_CO_17_exact()
    {
        var (pricing, _) = Pricing(CheckoutVatRounding.PerLine, new OrderDiscount(1.00m));
        var priced = await pricing.PriceAsync([new PricingInput("a", Item("x", 10.00m, basis: PriceBasis.Net), 1)], new Dictionary<string, string>(), null, false, default);
        var line = priced.Lines[0];
        // Goods paid: 12.10 − 1.00 = 11.10, the same base as a 1.00 line discount: net 9.17 + 1.93.
        line.Net.Should().Be(new Money(9.17m, "EUR"));
        line.Vat.Should().Be(new Money(1.93m, "EUR"));
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
