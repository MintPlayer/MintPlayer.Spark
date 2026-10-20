using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Pricing;
using static MintPlayer.Spark.Commerce.Tests.Core.CommerceFixtures;

namespace MintPlayer.Spark.Commerce.Tests.Core;

/// <summary>Round 5 §4.4: FinalPrice = unit price + VAT + every mandatory fee, computed once, exact to the cent.</summary>
public class FinalPriceCalculatorTests
{
    private static FinalPriceCalculator Calculator(SparkCommerceOptions? options = null)
        => new(new FixedOptionsMonitor<SparkCommerceOptions>(options ?? Options()));

    [Fact]
    public void A_gross_price_is_split_into_net_and_vat_that_add_up_exactly()
    {
        var price = Calculator().Calculate(new FinalPriceRequest("shop", Eur(12.10m), PriceBasis.Gross, 2100));

        price.Gross.Should().Be(Eur(12.10m));
        price.Net.Should().Be(Eur(10.00m));
        price.Vat.Should().Be(Eur(2.10m));
        (price.Net + price.Vat).Should().Be(price.Gross);
        price.Total.Should().Be(Eur(12.10m));
    }

    [Fact]
    public void A_net_price_gets_its_vat_rounded_once_half_away_from_zero()
    {
        // 0.05 × 21 % = 0.0105 → 0.01
        var price = Calculator().Calculate(new FinalPriceRequest("shop", Eur(0.05m), PriceBasis.Net, 2100));

        price.Vat.Should().Be(Eur(0.01m));
        price.Gross.Should().Be(Eur(0.06m));
    }

    [Fact]
    public void Mandatory_fees_are_in_the_final_price_and_optional_ones_are_not()
    {
        var price = Calculator().Calculate(new FinalPriceRequest("tickets", Eur(25m), Quantity: 2));

        // 2 × 25 + 2 × 1.50 service + 2.00 booking once
        price.Total.Should().Be(Eur(55.00m));
        price.MandatoryFees.Select(f => f.Code).Should().Equal("service", "booking");
        price.OptionalFees.Single().Code.Should().Be("insurance");
        price.OptionalFees.Single().Amount.Should().Be(Eur(2.50m)); // 5 % of 25, per unit, × 2
    }

    [Fact]
    public void The_unit_total_counts_per_order_fees_once()
    {
        var price = Calculator().Calculate(new FinalPriceRequest("tickets", Eur(25m), Quantity: 3));

        price.UnitTotal.Should().Be(Eur(28.50m)); // 25 + 1.50 + 2.00
    }

    [Fact]
    public void A_percentage_fee_is_rounded_per_unit_so_one_unit_times_n_equals_n_units()
    {
        var options = Options(o => o.Channels["pct"] = new CommerceChannelOptions
        {
            Fees = [new FeeRuleOptions { Code = "pct", PercentBasisPoints = 333 }],
        });
        var calculator = Calculator(options);

        var one = calculator.Calculate(new FinalPriceRequest("pct", Eur(9.99m)));
        var seven = calculator.Calculate(new FinalPriceRequest("pct", Eur(9.99m), Quantity: 7));

        (one.Total * 7).Should().Be(seven.Total);
    }

    [Fact]
    public void Extra_mandatory_charges_in_another_currency_are_refused()
    {
        var request = new FinalPriceRequest("shop", Eur(10m),
            ExtraMandatoryCharges: [new PriceComponent("x", FeeKind.Booking, new Money(1m, "USD"), true, "x")]);

        var act = () => Calculator().Calculate(request);

        act.Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void An_unknown_channel_is_refused_rather_than_priced_without_fees()
    {
        var act = () => Calculator().Calculate(new FinalPriceRequest("nope", Eur(10m)));

        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void A_fee_without_an_amount_for_the_currency_charges_nothing()
    {
        var price = Calculator().Calculate(new FinalPriceRequest("tickets", new Money(25m, "USD")));

        price.MandatoryFees.Should().BeEmpty();
        price.Total.Should().Be(new Money(25m, "USD"));
    }
}
