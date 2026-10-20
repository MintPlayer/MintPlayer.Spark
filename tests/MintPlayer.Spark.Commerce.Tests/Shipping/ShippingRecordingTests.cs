using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Shipping;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Commerce.Shipping.Rates;
using MintPlayer.Spark.Commerce.Shipping.Services;
using MintPlayer.Spark.Commerce.Shipping.Test;
using MintPlayer.Spark.Commerce.Tests.Payments;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Tests.Shipping;

/// <summary>
/// Shipping against RavenDB with the test carrier (D14): rates filtered by the catalog (GC9) with the customs choice
/// (GB6), multi-parcel labels as attachments and idempotent booking (GB5), customs declarations (GB6), service points
/// (GB5), return labels and the proof of dispatch (GB5, GA1), voids, and tracking deduplicated and ordered by event
/// time (X5, warehouse §4.12).
/// </summary>
public class ShippingRecordingTests : SparkTestDriver
{
    private readonly ManualTimeProvider clock = new(DateTimeOffset.Parse("2026-10-21T08:00:00Z"));

    private sealed class RecordingRecipient : IShipmentEventRecipient
    {
        public List<Shipment> Labelled { get; } = [];
        public List<Shipment> Returns { get; } = [];
        public List<ShipmentTrackingNotice> Tracking { get; } = [];

        public Task OnShipmentLabelledAsync(Shipment shipment, CancellationToken cancellationToken) { Labelled.Add(shipment); return Task.CompletedTask; }
        public Task OnReturnLabelCreatedAsync(Shipment returnShipment, CancellationToken cancellationToken) { Returns.Add(returnShipment); return Task.CompletedTask; }
        public Task OnTrackingChangedAsync(ShipmentTrackingNotice notice, CancellationToken cancellationToken) { Tracking.Add(notice); return Task.CompletedTask; }
    }

    private sealed record Host(IServiceProvider Services, RecordingRecipient Recipient, TestCarrierStore Carrier)
    {
        public ISparkShipping Shipping => Services.CreateScope().ServiceProvider.GetRequiredService<ISparkShipping>();
    }

    private static readonly ShippingAddress Shop = new() { Name = "Shop", Street = "Rue de la Loi", HouseNumber = "16", PostalCode = "1000", City = "Brussels", Country = "BE" };

    private static ShippingAddress Buyer(string country = "NL", string postalCode = "1012AB")
        => new() { Name = "Buyer", Street = "Damrak", HouseNumber = "1", PostalCode = postalCode, City = "Amsterdam", Country = country };

    private Host Build()
    {
        var settings = new SparkCommerceShippingOptions
        {
            Carrier = TestCarrier.CarrierName,
            Sender = Shop,
            Zones = new(StringComparer.OrdinalIgnoreCase)
            {
                ["eu"] = new() { Countries = ["BE", "NL", "LU", "FR", "DE"] },
                ["uk"] = new() { Countries = ["GB"], Incoterm = Incoterm.Ddp },
            },
            Methods = new(StringComparer.OrdinalIgnoreCase)
            {
                ["standard"] = new() { Service = "test-standard", Rates = new() { ["eu"] = 4.95m, ["uk"] = 14.95m }, TransitDays = new() { Min = 1, Max = 2 }, Order = 1 },
                ["batteries"] = new() { Service = "test-dg", Rates = new() { ["eu"] = 6.95m }, Order = 2 },
                ["point"] = new() { Service = "test-point", Rates = new() { ["eu"] = 3.95m }, Order = 3 },
            },
            Dispatch = new() { TimeZone = "Europe/Brussels" },
        };
        var store = new TestCarrierStore();
        var recipient = new RecordingRecipient();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton(Store);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IOptionsMonitor<SparkCommerceShippingOptions>>(new StaticOptionsMonitor<SparkCommerceShippingOptions>(settings));
        services.AddSingleton<IOptionsMonitor<TestShippingOptions>>(new StaticOptionsMonitor<TestShippingOptions>(new TestShippingOptions()));
        services.AddSingleton(store);
        services.AddScoped<TestCarrier>();
        services.AddScoped<IShippingCarrier>(sp => sp.GetRequiredService<TestCarrier>());
        services.AddScoped<IServicePointProvider>(sp => sp.GetRequiredService<TestCarrier>());
        services.AddScoped<IShippingRateProvider, ConfiguredShippingRateProvider>();
        services.AddScoped<ShippingCarrierRegistry>();
        services.AddScoped<DispatchSchedule>();
        services.AddScoped<ISparkShipping, SparkShipping>();
        services.AddSingleton<IShipmentEventRecipient>(recipient);
        return new Host(services.BuildServiceProvider(), recipient, store);
    }

    private static CreateShipmentRequest Request(string method = "standard", int parcels = 1, ShippingAddress? to = null) => new()
    {
        Reference = "orders/1",
        Method = method,
        ShipTo = to ?? Buyer(),
        Parcels = Enumerable.Range(1, parcels).Select(i => new ParcelRequest(800 * i)).ToList(),
    };

    [Fact]
    public async Task A_battery_basket_is_never_offered_a_service_that_refuses_lithium()
    {
        var host = Build();
        var rates = await host.Shipping.GetRatesAsync(new ShippingRateRequest
        {
            ShipTo = Buyer(),
            Subtotal = new Money(20m, "EUR"),
            WeightGrams = 900,
            DangerousGoods = [new DangerousGoodsItem { Class = DangerousGoodsClass.LithiumIonInEquipment }],
        }, default);
        rates.Select(r => r.Method).Should().Equal(["batteries"]);

        var plain = await host.Shipping.GetRatesAsync(new ShippingRateRequest { ShipTo = Buyer(), Subtotal = new Money(20m, "EUR"), WeightGrams = 900 }, default);
        plain.Select(r => r.Method).Should().Equal(["standard", "batteries", "point"]);
        plain.Single(r => r.Method == "point").ServicePoint.Should().BeTrue();
        // Wednesday 10:00 in Brussels, before the 15:00 cut-off: leaves today, one to two working days.
        plain.Single(r => r.Method == "standard").Promise!.From.Should().Be(new DateOnly(2026, 10, 22));
    }

    [Fact]
    public async Task A_destination_outside_the_customs_territory_shows_the_zone_incoterm()
    {
        var host = Build();
        var rates = await host.Shipping.GetRatesAsync(new ShippingRateRequest { ShipTo = Buyer("GB", "SW1A 1AA"), Subtotal = new Money(20m, "EUR"), WeightGrams = 900 }, default);
        var standard = rates.Single();
        standard.Customs.Should().Be(CustomsReason.OutsideCustomsTerritory);
        standard.Incoterm.Should().Be(Incoterm.Ddp);
    }

    [Fact]
    public async Task Each_parcel_gets_its_own_label_and_tracking_and_a_retry_books_nothing_twice()
    {
        var host = Build();
        var request = Request(parcels: 2) with { IdempotencyKey = "orders/1/outbound" };
        var shipment = await host.Shipping.CreateShipmentAsync(request, default);

        shipment.Id.Should().Be("Shipments/orders/1/outbound");
        shipment.Status.Should().Be(TrackingStatus.Labelled);
        shipment.Parcels.Should().HaveCount(2);
        shipment.Parcels.Select(p => p.TrackingNumber).Distinct().Should().HaveCount(2);
        shipment.PromisedFrom.Should().Be(new DateOnly(2026, 10, 22));
        var label = await host.Shipping.GetLabelAsync(shipment.Id!, 2, default);
        label!.MediaType.Should().Be(CarrierDocument.Pdf);
        System.Text.Encoding.ASCII.GetString(label.Content, 0, 5).Should().Be("%PDF-");

        var again = await host.Shipping.CreateShipmentAsync(request, default);
        again.Parcels.Select(p => p.TrackingNumber).Should().Equal(shipment.Parcels.Select(p => p.TrackingNumber));
        host.Carrier.Requests.Should().HaveCount(1);
        host.Recipient.Labelled.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_lithium_parcel_cannot_be_booked_on_a_service_that_refuses_it()
    {
        var host = Build();
        var refused = async () => await host.Shipping.CreateShipmentAsync(Request() with
        {
            DangerousGoods = [new DangerousGoodsItem { Class = DangerousGoodsClass.LithiumIonBatteries }],
        }, default);
        await refused.Should().ThrowAsync<ShippingRequestException>();
        host.Carrier.Requests.Should().BeEmpty();

        var booked = await host.Shipping.CreateShipmentAsync(Request("batteries") with
        {
            DangerousGoods = [new DangerousGoodsItem { Class = DangerousGoodsClass.LithiumIonInEquipment }],
        }, default);
        booked.DangerousGoods.Single().UnNumber.Should().Be("UN3481");
        // GC9: the marks reach the carrier with the booking.
        host.Carrier.Parcels.Values.Single().Marks.Should().Equal(["LITHIUM UN3481"]);
    }

    [Fact]
    public async Task A_shipment_leaving_the_customs_territory_needs_complete_customs_lines()
    {
        var host = Build();
        var withoutLines = async () => await host.Shipping.CreateShipmentAsync(Request(to: Buyer("GB", "SW1A 1AA")), default);
        await withoutLines.Should().ThrowAsync<ShippingRequestException>();

        var shipment = await host.Shipping.CreateShipmentAsync(Request(to: Buyer("GB", "SW1A 1AA")) with
        {
            InvoiceNumber = "INV-1",
            CustomsItems = [new CustomsItem { Description = "Cotton T-shirt", Quantity = 2, WeightGrams = 400, Value = new Money(40m, "EUR"), HsCode = "6109.10", OriginCountry = "pt" }],
        }, default);
        shipment.Customs!.Document.Should().Be(CustomsDocumentKind.Cn23);
        shipment.Customs.Incoterm.Should().Be(Incoterm.Ddp);
        shipment.Customs.Items.Single().HsCode.Should().Be("610910");
        shipment.Customs.Items.Single().OriginCountry.Should().Be("PT");
        (await host.Shipping.GetCustomsDocumentAsync(shipment.Id!, default)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_service_point_method_needs_a_chosen_point_which_travels_to_the_label()
    {
        var host = Build();
        var missing = async () => await host.Shipping.CreateShipmentAsync(Request("point"), default);
        await missing.Should().ThrowAsync<ShippingRequestException>();

        var points = await host.Shipping.SearchServicePointsAsync(null, new ServicePointQuery { Country = "nl", PostalCode = "1012ab" }, default);
        points.Should().HaveCount(3);
        var lockers = await host.Shipping.SearchServicePointsAsync(null, new ServicePointQuery { Country = "NL", PostalCode = "1012AB", Kind = ServicePointKind.ParcelLocker }, default);
        lockers.Should().ContainSingle();

        var shipment = await host.Shipping.CreateShipmentAsync(Request("point") with { ServicePoint = points[0] }, default);
        shipment.ServicePoint!.Id.Should().Be(points[0].Id);
        host.Carrier.Requests.Single().ServicePointId.Should().Be(points[0].Id);
    }

    [Fact]
    public async Task Tracking_is_deduplicated_ordered_by_event_time_and_final_when_delivered()
    {
        var host = Build();
        var shipment = await host.Shipping.CreateShipmentAsync(Request(), default);
        var number = shipment.Parcels[0].TrackingNumber!;
        var t1 = DateTimeOffset.Parse("2026-10-21T15:00:00Z");
        var t2 = DateTimeOffset.Parse("2026-10-22T09:00:00Z");
        var delivered = new CarrierTrackingEvent("e2", number, null, TrackingStatus.Delivered, "11", "Delivered", t2);
        var inTransit = new CarrierTrackingEvent("e1", number, null, TrackingStatus.InTransit, "3", "En route", t1);

        // Out of order and twice: the later event still wins, and the duplicate adds nothing.
        (await host.Shipping.RecordTrackingAsync(TestCarrier.CarrierName, [delivered], default)).Should().Be(1);
        (await host.Shipping.RecordTrackingAsync(TestCarrier.CarrierName, [inTransit, delivered], default)).Should().Be(1);
        (await host.Shipping.RecordTrackingAsync(TestCarrier.CarrierName, [new CarrierTrackingEvent("x", "UNKNOWN", null, TrackingStatus.Delivered, "11", null, t2)], default)).Should().Be(0);

        using var session = Store.OpenAsyncSession();
        var stored = await session.LoadAsync<Shipment>(shipment.Id);
        stored.Status.Should().Be(TrackingStatus.Delivered);
        stored.Final.Should().BeTrue();
        stored.Events.Select(e => e.EventId).Should().Equal(["e1", "e2"]);
        host.Recipient.Tracking.Should().ContainSingle();
    }

    [Fact]
    public async Task A_return_label_is_a_return_shipment_and_its_first_scan_is_the_proof_of_dispatch()
    {
        var host = Build();
        var outbound = await host.Shipping.CreateShipmentAsync(Request(), default);
        var returned = await host.Shipping.CreateReturnLabelAsync(new ReturnLabelRequest { ShipmentId = outbound.Id!, Reason = "withdrawal" }, default);

        returned.Direction.Should().Be(ShipmentDirection.Return);
        returned.ReturnOf.Should().Be(outbound.Id);
        returned.ServiceCode.Should().Be("test-return");
        returned.ShipFrom.Country.Should().Be("NL");
        returned.ShipTo.Country.Should().Be("BE");
        host.Recipient.Returns.Should().ContainSingle();

        var number = returned.Parcels[0].TrackingNumber!;
        await host.Shipping.RecordTrackingAsync(TestCarrier.CarrierName, [new CarrierTrackingEvent("r1", number, null, TrackingStatus.InTransit, "3", null, DateTimeOffset.Parse("2026-10-23T10:00:00Z"))], default);
        await host.Shipping.RecordTrackingAsync(TestCarrier.CarrierName, [new CarrierTrackingEvent("r2", number, null, TrackingStatus.Delivered, "11", null, DateTimeOffset.Parse("2026-10-24T10:00:00Z"))], default);
        host.Recipient.Tracking.Select(n => n.ProofOfDispatch).Should().Equal([true, false]);
    }

    [Fact]
    public async Task A_label_is_voided_only_before_the_carrier_has_the_parcel()
    {
        var host = Build();
        var first = await host.Shipping.CreateShipmentAsync(Request(), default);
        (await host.Shipping.VoidAsync(first.Id!, null, "users/staff", default)).Should().BeTrue();

        var second = await host.Shipping.CreateShipmentAsync(Request(), default);
        await host.Shipping.RecordTrackingAsync(TestCarrier.CarrierName,
            [new CarrierTrackingEvent("s1", second.Parcels[0].TrackingNumber!, null, TrackingStatus.InTransit, "3", null, DateTimeOffset.Parse("2026-10-21T16:00:00Z"))], default);
        (await host.Shipping.VoidAsync(second.Id!, null, "users/staff", default)).Should().BeFalse();

        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<Shipment>(first.Id)).Status.Should().Be(TrackingStatus.Cancelled);
        (await session.LoadAsync<Shipment>(second.Id)).Status.Should().Be(TrackingStatus.InTransit);
    }

    [Fact]
    public async Task Another_parcel_gets_its_own_label_in_shop_only_mode()
    {
        var host = Build();
        var shipment = await host.Shipping.CreateShipmentAsync(Request(), default);
        var grown = await host.Shipping.AddParcelAsync(shipment.Id!, new ParcelRequest(1500), "users/staff", default);
        grown.Parcels.Should().HaveCount(2);
        grown.Parcels[1].Number.Should().Be(2);
        (await host.Shipping.GetLabelAsync(shipment.Id!, 2, default)).Should().NotBeNull();
    }
}
