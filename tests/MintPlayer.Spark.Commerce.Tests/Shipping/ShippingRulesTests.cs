using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Shipping;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Commerce.Shipping.Rates;
using MintPlayer.Spark.Commerce.Shipping.Test;
using MintPlayer.Spark.Commerce.Tests.Payments;

namespace MintPlayer.Spark.Commerce.Tests.Shipping;

/// <summary>
/// The rules of Shipping without a database: customs (GB6), dangerous goods (GC9), zones and configured rates
/// (§4.9), startup validation, and the dispatch schedule with holidays (GC11).
/// </summary>
public class ShippingRulesTests
{
    private static ShippingAddress At(string country, string postalCode = "1000")
        => new() { Name = "A", Street = "Street", HouseNumber = "1", PostalCode = postalCode, City = "City", Country = country };

    private static readonly CustomsOptions Customs = new();

    [Theory]
    [InlineData("BE", "1000", "NL", "1012AB", CustomsReason.None)]
    [InlineData("BE", "1000", "GB", "SW1A 1AA", CustomsReason.OutsideCustomsTerritory)]
    [InlineData("BE", "1000", "CH", "8001", CustomsReason.OutsideCustomsTerritory)]
    [InlineData("CH", "8001", "CH", "3000", CustomsReason.None)]
    [InlineData("BE", "1000", "ES", "35001", CustomsReason.OutsideVatTerritory)]
    [InlineData("BE", "1000", "ES", "28001", CustomsReason.None)]
    [InlineData("BE", "1000", "DE", "27498", CustomsReason.OutsideCustomsTerritory)]
    [InlineData("BE", "1000", "GR", "63086", CustomsReason.OutsideVatTerritory)]
    [InlineData("GB", "SW1A 1AA", "BE", "1000", CustomsReason.OutsideCustomsTerritory)]
    public void Customs_apply_outside_the_EU_customs_or_VAT_territory(string fromCountry, string fromPostal, string toCountry, string toPostal, CustomsReason expected)
        => CustomsRules.Requirement(At(fromCountry, fromPostal), At(toCountry, toPostal), Customs).Should().Be(expected);

    [Fact]
    public void The_document_follows_the_service_style_and_the_declared_value()
    {
        CustomsRules.DocumentFor(CustomsDocumentStyle.LetterPost, new Money(250m, "EUR"), Customs).Should().Be(CustomsDocumentKind.Cn22);
        CustomsRules.DocumentFor(CustomsDocumentStyle.LetterPost, new Money(300m, "EUR"), Customs).Should().Be(CustomsDocumentKind.Cn22);
        CustomsRules.DocumentFor(CustomsDocumentStyle.LetterPost, new Money(300.01m, "EUR"), Customs).Should().Be(CustomsDocumentKind.Cn23);
        CustomsRules.DocumentFor(CustomsDocumentStyle.PostalParcel, new Money(10m, "EUR"), Customs).Should().Be(CustomsDocumentKind.Cn23);
        CustomsRules.DocumentFor(CustomsDocumentStyle.CommercialInvoice, new Money(10m, "EUR"), Customs).Should().Be(CustomsDocumentKind.CommercialInvoice);
    }

    [Fact]
    public void A_customs_line_needs_an_HS_code_an_origin_a_weight_and_a_description()
    {
        var declaration = new CustomsDeclaration
        {
            Items =
            [
                new CustomsItem { Description = "T-shirt", Quantity = 2, WeightGrams = 400, Value = new Money(30m, "EUR"), HsCode = "610910", OriginCountry = "PT" },
                new CustomsItem { Description = "", Quantity = 1, WeightGrams = 0, Value = new Money(5m, "EUR"), HsCode = "6109", OriginCountry = "Portugal" },
            ],
        };
        var problems = CustomsRules.Validate(declaration);
        problems.Should().HaveCount(4);
        problems.All(p => p.StartsWith("Item 2", StringComparison.Ordinal)).Should().BeTrue();
    }

    [Fact]
    public void Dangerous_goods_restrict_services_and_name_their_marks()
    {
        var items = DangerousGoodsRules.Normalize([new DangerousGoodsItem { Class = DangerousGoodsClass.LithiumIonInEquipment }, new DangerousGoodsItem { Class = DangerousGoodsClass.LimitedQuantity }]);
        items[0].UnNumber.Should().Be("UN3481");
        items[1].UnNumber.Should().BeNull();

        var standard = new CarrierService("std", "Standard");
        var dg = new CarrierService("dg", "DG") { AcceptedDangerousGoods = [DangerousGoodsClass.LithiumIonInEquipment, DangerousGoodsClass.LimitedQuantity] };
        DangerousGoodsRules.Accepts(standard, items).Should().BeFalse();
        DangerousGoodsRules.Accepts(dg, items).Should().BeTrue();
        DangerousGoodsRules.Accepts(standard, []).Should().BeTrue();
        DangerousGoodsRules.Marks(items).Should().BeEquivalentTo(["LITHIUM UN3481", "LQ"]);
    }

    [Fact]
    public void A_country_resolves_to_its_zone_then_to_the_wildcard()
    {
        var zones = new Dictionary<string, ShippingZoneOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["world"] = new() { Countries = ["*"] },
            ["benelux"] = new() { Countries = ["BE", "NL", "LU"] },
        };
        ShippingZones.Resolve(zones, "nl").Should().Be("benelux");
        ShippingZones.Resolve(zones, "JP").Should().Be("world");
        ShippingZones.Resolve(new Dictionary<string, ShippingZoneOptions> { ["eu"] = new() { Countries = ["BE"] } }, "JP").Should().BeNull();
    }

    private static SparkCommerceShippingOptions Settings() => new()
    {
        Carrier = "test",
        Zones = new(StringComparer.OrdinalIgnoreCase)
        {
            ["benelux"] = new() { Countries = ["BE", "NL", "LU"] },
            ["uk"] = new() { Countries = ["GB"], Incoterm = Incoterm.Ddp },
        },
        Methods = new(StringComparer.OrdinalIgnoreCase)
        {
            ["standard"] = new() { Service = "test-standard", Rates = new() { ["benelux"] = 4.95m, ["uk"] = 14.95m }, FreeFrom = new() { ["benelux"] = 50m }, Order = 1 },
            ["light"] = new() { Service = "test-letter", Rates = new() { ["benelux"] = 2.50m }, MaxWeightGrams = 2000, Order = 2 },
            ["pickup"] = new() { Kind = ShippingMethodKind.Pickup, Rates = new() { ["benelux"] = 0m }, PickupLocation = "The shop" },
        },
    };

    [Fact]
    public void Configured_rates_apply_the_free_threshold_inclusively_and_only_in_the_same_currency()
    {
        var settings = Settings();
        var below = ConfiguredShippingRateProvider.Rates(settings, new ShippingRateRequest { ShipTo = At("BE"), Subtotal = new Money(49.99m, "EUR"), WeightGrams = 500 });
        below.Single(r => r.Method == "standard").Price.Should().Be(new Money(4.95m, "EUR"));

        var at = ConfiguredShippingRateProvider.Rates(settings, new ShippingRateRequest { ShipTo = At("BE"), Subtotal = new Money(50m, "EUR"), WeightGrams = 500 });
        var standard = at.Single(r => r.Method == "standard");
        standard.Free.Should().BeTrue();
        standard.Price.IsZero.Should().BeTrue();

        var otherCurrency = ConfiguredShippingRateProvider.Rates(settings, new ShippingRateRequest { ShipTo = At("BE"), Subtotal = new Money(500m, "USD"), WeightGrams = 500 });
        otherCurrency.Single(r => r.Method == "standard").Free.Should().BeFalse();
    }

    [Fact]
    public void A_method_without_a_rate_for_the_zone_or_over_its_weight_is_not_offered()
    {
        var settings = Settings();
        var uk = ConfiguredShippingRateProvider.Rates(settings, new ShippingRateRequest { ShipTo = At("GB"), Subtotal = new Money(10m, "EUR") });
        uk.Select(r => r.Method).Should().BeEquivalentTo(["standard"]);

        var heavy = ConfiguredShippingRateProvider.Rates(settings, new ShippingRateRequest { ShipTo = At("BE"), Subtotal = new Money(10m, "EUR"), WeightGrams = 2500 });
        heavy.Select(r => r.Method).Should().NotContain("light");

        ConfiguredShippingRateProvider.Rates(settings, new ShippingRateRequest { ShipTo = At("JP"), Subtotal = new Money(10m, "EUR") }).Should().BeEmpty();
    }

    [Fact]
    public void Startup_refuses_unknown_carriers_services_zones_and_time_zones()
    {
        var carrier = new TestCarrier(new TestCarrierStore(), new StaticOptionsMonitor<TestShippingOptions>(new()), TimeProvider.System);
        SparkCommerceShippingExtensions.Problems([carrier], Settings()).Should().BeEmpty();
        SparkCommerceShippingExtensions.Problems([], Settings()).Any(p => p.StartsWith("No carrier", StringComparison.Ordinal)).Should().BeTrue();

        var broken = Settings();
        broken.Methods["standard"].Service = "nope";
        broken.Methods["light"].Rates["mars"] = 1m;
        broken.Dispatch.TimeZone = "Mars/Olympus";
        broken.Zones["benelux"].Countries.Add("Belgium");
        var problems = SparkCommerceShippingExtensions.Problems([carrier], broken);
        problems.Any(p => p.Contains("service 'nope'", StringComparison.Ordinal)).Should().BeTrue();
        problems.Any(p => p.Contains("zone 'mars'", StringComparison.Ordinal)).Should().BeTrue();
        problems.Any(p => p.Contains("Mars/Olympus", StringComparison.Ordinal)).Should().BeTrue();
        problems.Any(p => p.Contains("'Belgium'", StringComparison.Ordinal)).Should().BeTrue();
    }

    private sealed class FixedHolidays(params (string Country, DateOnly Date)[] days) : IShippingHolidayCalendar
    {
        public ValueTask<bool> IsHolidayAsync(string country, string? subdivision, DateOnly date, CancellationToken cancellationToken)
            => ValueTask.FromResult(days.Any(d => d.Country == country && d.Date == date));
    }

    private static DispatchSchedule Schedule(DateTimeOffset now, params (string, DateOnly)[] holidays)
    {
        var settings = new SparkCommerceShippingOptions { Dispatch = new DispatchOptions { TimeZone = "Europe/Brussels", CutOff = new TimeOnly(15, 0), HolidayCountry = "BE" } };
        return new DispatchSchedule(new StaticOptionsMonitor<SparkCommerceShippingOptions>(settings), [new FixedHolidays(holidays)], new ManualTimeProvider(now));
    }

    [Fact]
    public async Task The_cut_off_is_a_wall_clock_time_in_the_dispatch_zone()
    {
        // Wednesday 2026-10-21, 14:59 in Brussels (CEST, UTC+2) leaves today; 15:00 leaves Thursday.
        (await Schedule(DateTimeOffset.Parse("2026-10-21T12:59:00Z")).NextDispatchDateAsync(null, default)).Should().Be(new DateOnly(2026, 10, 21));
        (await Schedule(DateTimeOffset.Parse("2026-10-21T13:00:00Z")).NextDispatchDateAsync(null, default)).Should().Be(new DateOnly(2026, 10, 22));
    }

    [Fact]
    public async Task Dispatch_skips_weekends_and_public_holidays()
    {
        // Friday 2026-10-30 after the cut-off: Saturday and Sunday are no working days, Monday 2 November is a holiday
        // in this calendar (All Souls is not a Belgian public holiday; the fixed calendar only stands in for one).
        var schedule = Schedule(DateTimeOffset.Parse("2026-10-30T16:00:00Z"), ("BE", new DateOnly(2026, 11, 2)));
        (await schedule.NextDispatchDateAsync(null, default)).Should().Be(new DateOnly(2026, 11, 3));
    }

    [Fact]
    public async Task A_delivery_promise_counts_working_days_and_skips_the_destination_holidays()
    {
        // Thursday 2026-12-24 morning: leaves today; one to two days, but Friday 25 December is a holiday in NL.
        var schedule = Schedule(DateTimeOffset.Parse("2026-12-24T08:00:00Z"), ("NL", new DateOnly(2026, 12, 25)));
        var promise = await schedule.PromiseAsync(new TransitDaysOptions { Min = 1, Max = 2 }, At("NL", "1012AB"), null, default);
        promise.DispatchDate.Should().Be(new DateOnly(2026, 12, 24));
        promise.From.Should().Be(new DateOnly(2026, 12, 28));
        promise.By.Should().Be(new DateOnly(2026, 12, 29));
    }
}
