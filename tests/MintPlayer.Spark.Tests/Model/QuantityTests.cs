using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// <see cref="Quantity"/> (warehouse WX8): an amount in one unit, exact, never mixing units, with packaging
/// conversions through <see cref="PackagingLevels"/>; and the model, serializer and query plumbing it shares
/// with <see cref="Money"/> (commerce X4).
/// </summary>
public class QuantityTests
{
    // --- construction ----------------------------------------------------------------------------

    [Fact]
    public void A_quantity_normalizes_its_unit_and_drops_trailing_zeros()
    {
        var quantity = new Quantity(12.500m, " ea ");

        quantity.Unit.Should().Be("EA");
        quantity.Amount.Should().Be(12.5m);
        quantity.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Be("12.5");
        quantity.ToString().Should().Be("12.5 EA");
        new Quantity(12.0m, "EA").Should().Be(new Quantity(12m, "EA"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("E A")]
    [InlineData("KG/M")]
    [InlineData("ABCDEFGHIJKLMNOPQ")]
    public void An_invalid_unit_code_is_refused(string unit)
    {
        var act = () => new Quantity(1m, unit);

        act.Should().Throw<ArgumentException>();
        Quantity.IsValidUnit(unit).Should().BeFalse();
    }

    [Fact]
    public void Unit_codes_accept_letters_digits_underscore_and_dash()
    {
        Quantity.IsValidUnit("C62").Should().BeTrue();
        Quantity.IsValidUnit("pal_eu").Should().BeTrue();
        Quantity.IsValidUnit("x-1").Should().BeTrue();
        Quantity.IsValidUnit(null).Should().BeFalse();
    }

    // --- arithmetic ------------------------------------------------------------------------------

    [Fact]
    public void Same_unit_arithmetic_is_exact()
    {
        var a = new Quantity(0.1m, "KG");
        var b = new Quantity(0.2m, "KG");

        (a + b).Should().Be(new Quantity(0.3m, "KG"));
        (b - a).Should().Be(new Quantity(0.1m, "KG"));
        (-a).Should().Be(new Quantity(-0.1m, "KG"));
        (new Quantity(12m, "EA") * 3m).Should().Be(new Quantity(36m, "EA"));
        (3m * new Quantity(12m, "EA")).Should().Be(new Quantity(36m, "EA"));
        Quantity.Sum([a, b, a], "KG").Should().Be(new Quantity(0.4m, "KG"));
        Quantity.Sum([], "EA").IsZero.Should().BeTrue();
    }

    [Fact]
    public void Mixing_units_throws_instead_of_guessing()
    {
        var each = new Quantity(3m, "EA");
        var cases = new Quantity(2m, "CS");

        ((Action)(() => _ = each + cases)).Should().Throw<UnitMismatchException>();
        ((Action)(() => _ = each - cases)).Should().Throw<UnitMismatchException>();
        ((Action)(() => _ = each < cases)).Should().Throw<UnitMismatchException>();
        ((Action)(() => Quantity.Sum([each], "CS"))).Should().Throw<UnitMismatchException>();
        each.Equals(new Quantity(3m, "CS")).Should().BeFalse();
    }

    [Fact]
    public void Comparison_operators_compare_amounts_in_one_unit()
    {
        (new Quantity(2m, "EA") < new Quantity(3m, "EA")).Should().BeTrue();
        (new Quantity(3m, "EA") >= new Quantity(3.0m, "EA")).Should().BeTrue();
        (new Quantity(3m, "EA") > new Quantity(3m, "EA")).Should().BeFalse();
    }

    [Fact]
    public void CompareTo_is_a_total_order_by_unit_then_amount_with_null_first()
    {
        Quantity?[] values = [new Quantity(9m, "EA"), null, new Quantity(1m, "CS"), new Quantity(1m, "EA")];

        var sorted = values.Order(Comparer<Quantity?>.Create((x, y) => x is null ? (y is null ? 0 : -1) : x.CompareTo(y))).ToArray();

        sorted.Select(q => q?.ToString() ?? "null").Should().Equal("null", "1 CS", "1 EA", "9 EA");
    }

    // --- packaging levels ------------------------------------------------------------------------

    private static PackagingLevels Bottles() => new("EA", [new("CS", 12m), new("PAL", 480m)]);

    [Fact]
    public void Packaging_levels_convert_to_the_base_unit_exactly()
    {
        var levels = Bottles();

        levels.ToBase(new Quantity(2m, "cs")).Should().Be(new Quantity(24m, "EA"));
        levels.ToBase(new Quantity(1m, "PAL")).Should().Be(new Quantity(480m, "EA"));
        levels.ToBase(new Quantity(5m, "EA")).Should().Be(new Quantity(5m, "EA"));
        levels.Levels.Select(l => l.Unit).Should().Equal("PAL", "CS", "EA");
    }

    [Fact]
    public void Converting_up_a_level_is_exact_or_refused_never_rounded()
    {
        var levels = Bottles();

        levels.Convert(new Quantity(27m, "EA"), "CS").Should().Be(new Quantity(2.25m, "CS"));
        levels.Convert(new Quantity(1m, "PAL"), "CS").Should().Be(new Quantity(40m, "CS"));

        var thirds = new PackagingLevels("EA", [new("PK", 3m)]);
        thirds.TryConvert(new Quantity(1m, "EA"), "PK", out _).Should().BeFalse();
        ((Action)(() => thirds.Convert(new Quantity(1m, "EA"), "PK"))).Should().Throw<QuantityConversionException>();
    }

    [Fact]
    public void An_unknown_unit_is_a_conversion_error()
    {
        var levels = Bottles();

        levels.Knows("cs").Should().BeTrue();
        levels.Knows("KG").Should().BeFalse();
        ((Action)(() => levels.ToBase(new Quantity(1m, "KG")))).Should().Throw<QuantityConversionException>();
    }

    [Fact]
    public void Breakdown_shows_whole_packages_largest_first_and_sums_back()
    {
        var levels = Bottles();

        levels.Breakdown(new Quantity(507m, "EA")).Select(q => q.ToString()).Should().Equal("1 PAL", "2 CS", "3 EA");
        levels.Breakdown(new Quantity(24m, "EA")).Select(q => q.ToString()).Should().Equal("2 CS");
        levels.Breakdown(new Quantity(0m, "EA")).Select(q => q.ToString()).Should().Equal("0 EA");
        levels.Breakdown(new Quantity(-13m, "EA")).Select(q => q.ToString()).Should().Equal("-1 CS", "-1 EA");
        levels.Breakdown(new Quantity(2.5m, "CS")).Select(q => q.ToString()).Should().Equal("2 CS", "6 EA");
        levels.Sum(levels.Breakdown(new Quantity(507m, "EA"))).Should().Be(new Quantity(507m, "EA"));
    }

    [Fact]
    public void Packaging_levels_compare_and_add_across_units()
    {
        var levels = Bottles();

        levels.AreEqual(new Quantity(1m, "CS"), new Quantity(12m, "EA")).Should().BeTrue();
        (levels.Compare(new Quantity(1m, "CS"), new Quantity(13m, "EA")) < 0).Should().BeTrue();
        levels.Sum([new Quantity(2m, "CS"), new Quantity(3m, "EA")]).Should().Be(new Quantity(27m, "EA"));
    }

    [Fact]
    public void Weight_levels_may_be_smaller_than_the_base_unit()
    {
        var weight = new PackagingLevels("KG", [new("G", 0.001m)]);

        weight.ToBase(new Quantity(250m, "G")).Should().Be(new Quantity(0.25m, "KG"));
        weight.Convert(new Quantity(1.5m, "KG"), "G").Should().Be(new Quantity(1500m, "G"));
    }

    [Theory]
    [InlineData("CS", 0)]
    [InlineData("CS", -1)]
    [InlineData("EA", 2)]
    public void An_invalid_packaging_table_is_refused(string unit, int baseQuantity)
    {
        var act = () => new PackagingLevels("EA", [new(unit, baseQuantity)]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_level_listed_twice_is_refused()
    {
        var act = () => new PackagingLevels("EA", [new("CS", 12m), new("cs", 6m)]);

        act.Should().Throw<ArgumentException>();
    }

    // --- one stored shape ------------------------------------------------------------------------

    private sealed class Holder
    {
        public Quantity? OnHand { get; set; }
    }

    [Fact]
    public void System_Text_Json_writes_the_CLR_names_whatever_the_naming_policy()
    {
        var quantity = new Quantity(12.50m, "EA");

        JsonSerializer.Serialize(quantity).Should().Be("{\"Amount\":12.5,\"Unit\":\"EA\"}");
        JsonSerializer.Serialize(quantity, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Should().Be("{\"Amount\":12.5,\"Unit\":\"EA\"}");
    }

    [Theory]
    [InlineData("{\"Amount\":12.5,\"Unit\":\"EA\"}")]
    [InlineData("{\"amount\":12.50,\"unit\":\"ea\"}")]
    [InlineData("{\"Amount\":\"12.5\",\"Unit\":\"EA\",\"Ignored\":{\"x\":1}}")]
    public void System_Text_Json_reads_the_shape_leniently(string json)
        => JsonSerializer.Deserialize<Quantity>(json).Should().Be(new Quantity(12.5m, "EA"));

    [Theory]
    [InlineData("{\"Amount\":1,\"Unit\":\"E A\"}")]
    [InlineData("{\"Amount\":1}")]
    [InlineData("{\"Unit\":\"EA\"}")]
    [InlineData("\"12 EA\"")]
    public void System_Text_Json_refuses_what_is_not_a_quantity(string json)
    {
        var act = () => JsonSerializer.Deserialize<Quantity>(json);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Newtonsoft_the_RavenDB_serializer_writes_and_reads_the_same_shape()
    {
        var converterType = typeof(IEntityMapper).Assembly.GetType("MintPlayer.Spark.Converters.QuantityNewtonsoftJsonConverter")
            ?? throw new InvalidOperationException("QuantityNewtonsoftJsonConverter not found.");
        var settings = new Newtonsoft.Json.JsonSerializerSettings();
        settings.Converters.Add((Newtonsoft.Json.JsonConverter)Activator.CreateInstance(converterType)!);

        var json = Newtonsoft.Json.JsonConvert.SerializeObject(new Holder { OnHand = new Quantity(12.5m, "EA") }, settings);

        json.Should().Be("{\"OnHand\":{\"Amount\":12.5,\"Unit\":\"EA\"}}");
        Newtonsoft.Json.JsonConvert.DeserializeObject<Holder>(json, settings)!.OnHand.Should().Be(new Quantity(12.5m, "EA"));
        (Newtonsoft.Json.JsonConvert.DeserializeObject<Holder>("{\"OnHand\":null}", settings)!.OnHand is null).Should().BeTrue();
        ((Action)(() => Newtonsoft.Json.JsonConvert.DeserializeObject<Holder>("{\"OnHand\":{\"Amount\":1,\"Unit\":\"E A\"}}", settings)))
            .Should().Throw<Newtonsoft.Json.JsonSerializationException>();
    }

    // --- the model --------------------------------------------------------------------------------

    [Fact]
    public void Quantity_is_its_own_data_type_never_AsDetail()
    {
        SparkModelShape.GetDataType(typeof(Quantity)).Should().Be(SparkQuantity.DataType);
        SparkModelShape.IsComplexType(typeof(Quantity)).Should().BeFalse();
        SparkModelShape.GetDataType(typeof(List<Quantity>)).Should().Be(SparkQuantity.DataType);
    }

    [Fact]
    public void Model_sync_gives_a_Quantity_attribute_the_core_renderer_unless_it_has_one()
    {
        var applySeeds = typeof(ModelSynchronizer).GetMethod("ApplyRendererSeeds", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ModelSynchronizer.ApplyRendererSeeds was renamed or removed.");
        var definition = new EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = nameof(StockLine),
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "OnHand", DataType = SparkQuantity.DataType },
                new() { Id = Guid.NewGuid(), Name = "Reserved", DataType = SparkQuantity.DataType, Renderer = "app-quantity" },
                new() { Id = Guid.NewGuid(), Name = "Sku", DataType = "string" },
            ],
        };

        applySeeds.Invoke(null, [definition, typeof(StockLine)]);

        definition.Attributes.Select(a => a.Renderer).Should().Equal(SparkQuantity.RendererName, "app-quantity", null);
    }

    [Fact]
    public void The_mapper_reads_a_wire_value_and_reports_an_invalid_one_as_a_validation_error()
    {
        EntityMapper.ReadQuantity(JsonDocument.Parse("{\"amount\":12,\"unit\":\"cs\"}").RootElement, "OnHand")
            .Should().Be(new Quantity(12m, "CS"));
        (EntityMapper.ReadQuantity(JsonDocument.Parse("null").RootElement, "OnHand") is null).Should().BeTrue();
        (EntityMapper.ReadQuantity("", "OnHand") is null).Should().BeTrue();

        var act = () => EntityMapper.ReadQuantity(JsonDocument.Parse("{\"Amount\":1,\"Unit\":\"E A\"}").RootElement, "OnHand");
        act.Should().Throw<SparkValidationException>().WithMessage("*OnHand*");
    }

    // --- query: sort and filter by unit and amount -----------------------------------------------

    public sealed class StockLine
    {
        public string? Sku { get; set; }
        public Quantity? OnHand { get; set; }
    }

    public sealed class StockLineRow
    {
        public Quantity? OnHand { get; set; }
        [IgnoreProperty] public decimal? OnHandAmount { get; set; }
        [IgnoreProperty] public string? OnHandUnit { get; set; }
    }

    private static readonly PropertyInfo OnHandProperty = typeof(StockLine).GetProperty(nameof(StockLine.OnHand))!;

    private static readonly StockLine[] Lines =
    [
        new() { Sku = "b", OnHand = new Quantity(9m, "EA") },
        new() { Sku = "none" },
        new() { Sku = "c", OnHand = new Quantity(5m, "KG") },
        new() { Sku = "a", OnHand = new Quantity(1m, "EA") },
    ];

    [Fact]
    public void In_memory_a_quantity_column_sorts_by_unit_then_amount_and_a_missing_value_does_not_throw()
    {
        var row = Expression.Parameter(typeof(StockLine), "x");
        IQueryable<StockLine> query = Lines.AsQueryable();

        var keys = AmountQueryFields.SortKeys(row, OnHandProperty, inMemory: true);
        var ordered = query.OrderBy((Expression<Func<StockLine, string?>>)Expression.Lambda(keys[0], row))
            .ThenBy((Expression<Func<StockLine, decimal?>>)Expression.Lambda(keys[1], row));

        ordered.Select(l => l.Sku).Should().Equal("none", "a", "b", "c");
    }

    [Fact]
    public void In_memory_a_quantity_filter_matches_unit_and_amount_or_no_value()
    {
        var row = Expression.Parameter(typeof(StockLine), "x");
        Func<StockLine, bool> Matches(Quantity? value)
            => ((Expression<Func<StockLine, bool>>)Expression.Lambda(AmountQueryFields.EqualsValue(row, OnHandProperty, value, inMemory: true), row)).Compile();

        Lines.Where(Matches(new Quantity(9m, "EA"))).Select(l => l.Sku).Should().Equal("b");
        Lines.Where(Matches(new Quantity(9m, "KG"))).Should().BeEmpty();
        Lines.Where(Matches(null)).Select(l => l.Sku).Should().Equal("none");
    }

    [Fact]
    public void A_filter_value_off_the_wire_converts_to_a_quantity_or_narrows_to_nothing()
    {
        var tryConvert = typeof(QueryExecutor).GetMethod("TryConvertFilterValue", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("QueryExecutor.TryConvertFilterValue was renamed or removed.");
        (bool Ok, object? Value) Convert(string json)
        {
            object?[] args = [JsonDocument.Parse(json).RootElement.Clone(), typeof(Quantity), null];
            return ((bool)tryConvert.Invoke(null, args)!, args[2]);
        }

        Convert("{\"Amount\":9.5,\"Unit\":\"EA\"}").Should().Be((true, (object?)new Quantity(9.5m, "EA")));
        Convert("null").Should().Be((true, (object?)null));
        Convert("{\"Amount\":1,\"Unit\":\"E A\"}").Ok.Should().BeFalse();
        Convert("\"9.5 EA\"").Ok.Should().BeFalse();
    }

    [Fact]
    public void A_projection_that_declares_both_companions_is_sorted_through_them()
    {
        var onHand = typeof(StockLineRow).GetProperty(nameof(StockLineRow.OnHand))!;
        var row = Expression.Parameter(typeof(StockLineRow), "x");

        var keys = AmountQueryFields.SortKeys(row, onHand, inMemory: false);

        keys.Select(k => ((MemberExpression)k).Member.Name).Should().Equal("OnHandUnit", "OnHandAmount");
        AmountQueryFields.IsBacked(typeof(StockLineRow), onHand, new HashSet<string> { "OnHand", "OnHandAmount", "OnHandUnit" })
            .Should().BeTrue();
        AmountQueryFields.IsBacked(typeof(StockLine), OnHandProperty, new HashSet<string> { "OnHand" }).Should().BeFalse();
        AmountQueryFields.IsBacked(typeof(StockLine), OnHandProperty, null).Should().BeTrue();
        SparkQuantity.AmountCompanion("OnHand").Should().Be("OnHandAmount");
        SparkQuantity.UnitCompanion("OnHand").Should().Be("OnHandUnit");
    }
}
