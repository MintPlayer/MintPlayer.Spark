using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// <see cref="Money"/> (commerce X4): exact by construction, one currency at a time, and two rounding
/// primitives (<see cref="Money.RoundDiv(long, long)"/>, <see cref="Money.Allocate(long, IReadOnlyList{long})"/>)
/// that spike S21 measured against 10,000 orders. The order-level rules built on them are covered by
/// <see cref="MoneyRoundingPropertyTests"/>.
/// </summary>
public class MoneyTests
{
    // --- construction ----------------------------------------------------------------------------

    [Fact]
    public void An_amount_carries_exactly_its_currencys_decimals()
    {
        var price = new Money(12.3m, "eur");

        price.Currency.Should().Be("EUR");
        price.Amount.Should().Be(12.30m);
        price.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Be("12.30");
        price.ToMinorUnits().Should().Be(1230);
    }

    [Theory]
    [InlineData("EUR", 2)]
    [InlineData("JPY", 0)]
    [InlineData("KWD", 3)]
    [InlineData("CLF", 4)]
    [InlineData("usd", 2)]
    public void Minor_units_follow_ISO_4217(string currency, int digits)
        => Money.MinorUnitDigits(currency).Should().Be(digits);

    [Fact]
    public void A_more_precise_amount_is_refused_never_rounded()
    {
        var act = () => new Money(12.345m, "EUR");

        act.Should().Throw<ArgumentException>().WithMessage("*more decimals*RoundDiv*");
        // Trailing zeros are not precision.
        new Money(12.3400m, "EUR").ToMinorUnits().Should().Be(1234);
        new Money(1230m, "JPY").ToMinorUnits().Should().Be(1230);
        ((Action)(() => new Money(1230.5m, "JPY"))).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("XYZ")]
    [InlineData("EURO")]
    [InlineData("XAU")] // gold has no minor units: not money a shop charges
    [InlineData("")]
    public void An_unknown_currency_is_refused(string currency)
    {
        var act = () => new Money(1m, currency);

        act.Should().Throw<ArgumentException>();
        Money.IsKnownCurrency(currency).Should().BeFalse();
    }

    [Fact]
    public void An_amount_beyond_64_bits_of_minor_units_is_refused()
    {
        var act = () => new Money(decimal.MaxValue, "EUR");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Minor_units_round_trip_including_negatives()
    {
        Money.FromMinorUnits(-1999, "EUR").Amount.Should().Be(-19.99m);
        Money.FromMinorUnits(5, "KWD").Amount.Should().Be(0.005m);
        Money.Zero("USD").IsZero.Should().BeTrue();
    }

    // --- arithmetic --------------------------------------------------------------------------------

    [Fact]
    public void Arithmetic_is_exact_within_one_currency()
    {
        var unit = new Money(19.99m, "EUR");

        (unit * 3).Should().Be(new Money(59.97m, "EUR"));
        (3 * unit).Should().Be(new Money(59.97m, "EUR"));
        (unit + new Money(0.01m, "EUR")).Should().Be(new Money(20m, "EUR"));
        (unit - new Money(20m, "EUR")).Should().Be(new Money(-0.01m, "EUR"));
        (-unit).Amount.Should().Be(-19.99m);
        Money.Sum([unit, unit], "EUR").Should().Be(new Money(39.98m, "EUR"));
        Money.Sum([], "EUR").Should().Be(Money.Zero("EUR"));
    }

    [Fact]
    public void Mixing_currencies_throws()
    {
        var eur = new Money(1m, "EUR");
        var usd = new Money(1m, "USD");

        ((Action)(() => _ = eur + usd)).Should().Throw<CurrencyMismatchException>();
        ((Action)(() => _ = eur - usd)).Should().Throw<CurrencyMismatchException>();
        ((Action)(() => _ = eur < usd)).Should().Throw<CurrencyMismatchException>();
        ((Action)(() => Money.Sum([eur, usd], "EUR"))).Should().Throw<CurrencyMismatchException>();
        ((Action)(() => eur.Allocate([usd]))).Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void Equality_is_value_equality_and_sorting_is_currency_then_amount()
    {
        new Money(12.3m, "EUR").Should().Be(new Money(12.30m, "eur"));
        new Money(12.3m, "EUR").GetHashCode().Should().Be(new Money(12.30m, "EUR").GetHashCode());
        (new Money(1m, "EUR") == new Money(1m, "USD")).Should().BeFalse();

        // CompareTo is the grid's total order and never throws, unlike the operators.
        var sorted = new[] { new Money(5m, "USD"), new Money(9m, "EUR"), new Money(1m, "EUR") }.Order().ToList();
        sorted.Should().Equal(new Money(1m, "EUR"), new Money(9m, "EUR"), new Money(5m, "USD"));
        (new Money(1m, "EUR") < new Money(2m, "EUR")).Should().BeTrue();
    }

    // --- RoundDiv: half away from zero, once -------------------------------------------------------

    [Theory]
    [InlineData(5, 2, 3)]
    [InlineData(-5, 2, -3)]
    [InlineData(249, 100, 2)]
    [InlineData(250, 100, 3)]
    [InlineData(-250, 100, -3)]
    [InlineData(5, -2, -3)]
    [InlineData(0, 7, 0)]
    [InlineData(1999 * 21, 100, 420)] // 21% VAT on 19.99: 419.79 → 420
    [InlineData(2419 * 21, 121, 420)] // the VAT inside a 24.19 gross at 21%: 419.83 → 420
    public void RoundDiv_rounds_half_away_from_zero(long numerator, long denominator, long expected)
        => Money.RoundDiv(numerator, denominator).Should().Be(expected);

    [Fact]
    public void RoundDiv_does_not_overflow_on_a_product_and_refuses_zero()
    {
        Money.RoundDiv((Int128)long.MaxValue * 21, 21).Should().Be((Int128)long.MaxValue);
        ((Action)(() => Money.RoundDiv(1, 0))).Should().Throw<DivideByZeroException>();
    }

    [Fact]
    public void Share_is_a_single_rounded_quotient()
    {
        var price = new Money(19.99m, "EUR");

        price.Share(15, 100).Should().Be(new Money(3.00m, "EUR"));     // 2.9985 → 3.00
        price.Share(1250, 10_000).Should().Be(new Money(2.50m, "EUR")); // 2.49875 → 2.50
        new Money(24.19m, "EUR").Share(21, 121).Should().Be(new Money(4.20m, "EUR"));
    }

    // --- Allocate: largest remainder, sums exactly -------------------------------------------------

    [Fact]
    public void Allocate_sums_exactly_and_each_part_is_within_one_unit_of_its_share()
    {
        var parts = Money.Allocate(100, [1, 1, 1]);

        parts.Sum().Should().Be(100);
        parts.Should().Equal(34, 33, 33); // tie on remainder and weight: the earlier index
    }

    [Fact]
    public void Allocate_breaks_ties_by_larger_weight_before_index()
    {
        // 5 over [1, 2, 2]: exact shares 1, 2, 2 — no remainder; 7 over [1, 2, 2]: 1.4, 2.8, 2.8.
        Money.Allocate(7, [1, 2, 2]).Should().Equal(1, 3, 3);
        // 2 over [1, 3]: 0.5, 1.5 — equal remainders, the larger weight wins.
        Money.Allocate(2, [1, 3]).Should().Equal(0, 2);
    }

    [Fact]
    public void Allocate_gives_a_zero_weight_nothing_and_never_more_than_a_weight()
    {
        var weights = new long[] { 0, 3, 7, 0, 1 };

        var parts = Money.Allocate(11, weights);

        parts.Should().Equal(0, 3, 7, 0, 1);
        Money.Allocate(5, weights).Zip(weights).All(p => p.First <= p.Second).Should().BeTrue();
    }

    [Fact]
    public void Allocate_mirrors_a_negative_total()
        => Money.Allocate(-100, [1, 1, 1]).Should().Equal(-34, -33, -33);

    [Fact]
    public void Allocate_refuses_negative_weights_and_all_zero_weights()
    {
        ((Action)(() => Money.Allocate(1, [1, -1]))).Should().Throw<ArgumentException>();
        ((Action)(() => Money.Allocate(1, [0, 0]))).Should().Throw<ArgumentException>();
        Money.Allocate(0, [0, 0]).Should().Equal(0, 0);
    }

    [Fact]
    public void Allocating_money_over_amounts_keeps_the_currency()
    {
        var discount = new Money(10m, "EUR");

        var parts = discount.Allocate([new Money(19.99m, "EUR"), new Money(5m, "EUR"), new Money(0m, "EUR")]);

        parts.Should().Equal(new Money(8.00m, "EUR"), new Money(2.00m, "EUR"), new Money(0m, "EUR"));
        Money.Sum(parts, "EUR").Should().Be(discount);
    }

    // --- one stored shape ------------------------------------------------------------------------

    [Fact]
    public void System_Text_Json_writes_the_CLR_names_whatever_the_naming_policy()
    {
        var price = new Money(12.3m, "EUR");

        JsonSerializer.Serialize(price).Should().Be("{\"Amount\":12.30,\"Currency\":\"EUR\"}");
        JsonSerializer.Serialize(price, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Should().Be("{\"Amount\":12.30,\"Currency\":\"EUR\"}");
    }

    [Theory]
    [InlineData("{\"Amount\":12.30,\"Currency\":\"EUR\"}")]
    [InlineData("{\"amount\":12.3,\"currency\":\"eur\"}")]
    [InlineData("{\"Amount\":\"12.30\",\"Currency\":\"EUR\",\"Ignored\":{\"x\":1}}")]
    public void System_Text_Json_reads_the_shape_leniently(string json)
        => JsonSerializer.Deserialize<Money>(json).Should().Be(new Money(12.30m, "EUR"));

    [Theory]
    [InlineData("{\"Amount\":12.345,\"Currency\":\"EUR\"}")]
    [InlineData("{\"Amount\":1,\"Currency\":\"XYZ\"}")]
    [InlineData("{\"Amount\":1}")]
    [InlineData("\"12.30 EUR\"")]
    public void System_Text_Json_refuses_what_is_not_exact_money(string json)
    {
        var act = () => JsonSerializer.Deserialize<Money>(json);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Newtonsoft_the_RavenDB_serializer_writes_and_reads_the_same_shape()
    {
        var converterType = typeof(IEntityMapper).Assembly.GetType("MintPlayer.Spark.Converters.MoneyNewtonsoftJsonConverter")
            ?? throw new InvalidOperationException("MoneyNewtonsoftJsonConverter not found.");
        var settings = new Newtonsoft.Json.JsonSerializerSettings();
        settings.Converters.Add((Newtonsoft.Json.JsonConverter)Activator.CreateInstance(converterType)!);

        var json = Newtonsoft.Json.JsonConvert.SerializeObject(new Holder { Price = new Money(12.3m, "EUR") }, settings);

        json.Should().Be("{\"Price\":{\"Amount\":12.30,\"Currency\":\"EUR\"}}");
        Newtonsoft.Json.JsonConvert.DeserializeObject<Holder>(json, settings)!.Price.Should().Be(new Money(12.30m, "EUR"));
        (Newtonsoft.Json.JsonConvert.DeserializeObject<Holder>("{\"Price\":null}", settings)!.Price is null).Should().BeTrue();
        ((Action)(() => Newtonsoft.Json.JsonConvert.DeserializeObject<Holder>("{\"Price\":{\"Amount\":1.001,\"Currency\":\"EUR\"}}", settings)))
            .Should().Throw<Newtonsoft.Json.JsonSerializationException>();
    }

    private sealed class Holder
    {
        public Money? Price { get; set; }
    }

    // --- the model --------------------------------------------------------------------------------

    [Fact]
    public void Money_is_its_own_data_type_never_AsDetail()
    {
        SparkModelShape.GetDataType(typeof(Money)).Should().Be(SparkMoney.DataType);
        SparkModelShape.IsComplexType(typeof(Money)).Should().BeFalse();
        SparkModelShape.GetDataType(typeof(List<Money>)).Should().Be(SparkMoney.DataType);
    }

    [Fact]
    public void Model_sync_gives_a_Money_attribute_the_core_renderer_unless_it_has_one()
    {
        var applySeeds = typeof(ModelSynchronizer).GetMethod("ApplyRendererSeeds", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ModelSynchronizer.ApplyRendererSeeds was renamed or removed.");
        var definition = new EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = nameof(Product),
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Price", DataType = SparkMoney.DataType },
                new() { Id = Guid.NewGuid(), Name = "Fee", DataType = SparkMoney.DataType, Renderer = "app-money" },
                new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string" },
            ],
        };

        applySeeds.Invoke(null, [definition, typeof(Product)]);

        definition.Attributes.Select(a => a.Renderer).Should().Equal(SparkMoney.RendererName, "app-money", null);
    }

    [Fact]
    public void The_mapper_reads_a_wire_value_and_reports_an_inexact_one_as_a_validation_error()
    {
        EntityMapper.ReadMoney(JsonDocument.Parse("{\"amount\":12.3,\"currency\":\"EUR\"}").RootElement, "Price")
            .Should().Be(new Money(12.30m, "EUR"));
        (EntityMapper.ReadMoney(JsonDocument.Parse("null").RootElement, "Price") is null).Should().BeTrue();
        (EntityMapper.ReadMoney("", "Price") is null).Should().BeTrue();

        var act = () => EntityMapper.ReadMoney(JsonDocument.Parse("{\"Amount\":12.345,\"Currency\":\"EUR\"}").RootElement, "Price");
        act.Should().Throw<SparkValidationException>().WithMessage("*Price*");
    }

    // --- query: sort and filter by currency and amount -------------------------------------------

    public sealed class Product
    {
        public string? Name { get; set; }
        public Money? Price { get; set; }
    }

    public sealed class ProductRow
    {
        public Money? Price { get; set; }
        [IgnoreProperty] public decimal? PriceAmount { get; set; }
        [IgnoreProperty] public string? PriceCurrency { get; set; }
    }

    private static readonly PropertyInfo PriceProperty = typeof(Product).GetProperty(nameof(Product.Price))!;

    private static readonly Product[] Products =
    [
        new() { Name = "b", Price = new Money(9m, "EUR") },
        new() { Name = "none" },
        new() { Name = "c", Price = new Money(5m, "USD") },
        new() { Name = "a", Price = new Money(1m, "EUR") },
    ];

    [Fact]
    public void In_memory_a_money_column_sorts_by_currency_then_amount_and_a_missing_value_does_not_throw()
    {
        var row = Expression.Parameter(typeof(Product), "x");
        IQueryable<Product> query = Products.AsQueryable();

        var keys = MoneyQueryFields.SortKeys(row, PriceProperty, inMemory: true);
        var ordered = query.OrderBy((Expression<Func<Product, string?>>)Expression.Lambda(keys[0], row))
            .ThenBy((Expression<Func<Product, decimal?>>)Expression.Lambda(keys[1], row));

        ordered.Select(p => p.Name).Should().Equal("none", "a", "b", "c");
    }

    [Fact]
    public void A_filter_value_off_the_wire_converts_to_money_or_narrows_to_nothing()
    {
        var tryConvert = typeof(QueryExecutor).GetMethod("TryConvertFilterValue", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("QueryExecutor.TryConvertFilterValue was renamed or removed.");
        (bool Ok, object? Value) Convert(string json)
        {
            object?[] args = [JsonDocument.Parse(json).RootElement.Clone(), typeof(Money), null];
            return ((bool)tryConvert.Invoke(null, args)!, args[2]);
        }

        Convert("{\"Amount\":9.5,\"Currency\":\"EUR\"}").Should().Be((true, (object?)new Money(9.50m, "EUR")));
        Convert("null").Should().Be((true, (object?)null));
        Convert("{\"Amount\":9.555,\"Currency\":\"EUR\"}").Ok.Should().BeFalse();
        Convert("\"9.50 EUR\"").Ok.Should().BeFalse();
    }

    [Fact]
    public void In_memory_a_money_filter_matches_currency_and_amount_or_no_value()
    {
        var row = Expression.Parameter(typeof(Product), "x");
        Func<Product, bool> Matches(Money? value)
            => ((Expression<Func<Product, bool>>)Expression.Lambda(MoneyQueryFields.EqualsValue(row, PriceProperty, value, inMemory: true), row)).Compile();

        Products.Where(Matches(new Money(9m, "EUR"))).Select(p => p.Name).Should().Equal("b");
        Products.Where(Matches(new Money(9m, "USD"))).Should().BeEmpty();
        Products.Where(Matches(null)).Select(p => p.Name).Should().Equal("none");
    }

    [Fact]
    public void A_projection_that_declares_both_companions_is_sorted_through_them()
    {
        var price = typeof(ProductRow).GetProperty(nameof(ProductRow.Price))!;
        var row = Expression.Parameter(typeof(ProductRow), "x");

        var keys = MoneyQueryFields.SortKeys(row, price, inMemory: false);

        keys.Select(k => ((MemberExpression)k).Member.Name).Should().Equal("PriceCurrency", "PriceAmount");
        MoneyQueryFields.IsBacked(typeof(ProductRow), price, new HashSet<string> { "Price", "PriceAmount", "PriceCurrency" })
            .Should().BeTrue();
        // A static index without the companions cannot sort a Money column; a dynamic one can.
        MoneyQueryFields.IsBacked(typeof(Product), PriceProperty, new HashSet<string> { "Price" }).Should().BeFalse();
        MoneyQueryFields.IsBacked(typeof(Product), PriceProperty, null).Should().BeTrue();
        SparkMoney.AmountCompanion("Price").Should().Be("PriceAmount");
        SparkMoney.CurrencyCompanion("Price").Should().Be("PriceCurrency");
    }
}
