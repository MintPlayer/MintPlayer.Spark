using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Extensions.Primitives;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Shipping;
using MintPlayer.Spark.Commerce.Shipping.Bpost;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Commerce.Shipping.PostNL;
using MintPlayer.Spark.Commerce.Shipping.Sendcloud;
using MintPlayer.Spark.Commerce.Tests.Payments;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Tests.Shipping;

/// <summary>The carrier fixtures (Shipping/Fixtures, copied to the output). See Fixtures/README.md.</summary>
internal static class CarrierFixtures
{
    public static string Text(string carrier, string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shipping", "Fixtures", carrier, name));

    public static JsonElement Json(string carrier, string name)
    {
        using var document = JsonDocument.Parse(Text(carrier, name));
        return document.RootElement.Clone();
    }

    public static XDocument Xml(string carrier, string name) => XDocument.Parse(Text(carrier, name));
}

/// <summary>
/// Spike S13, fixture-based (round 4; round 5: service points, return labels, multi-parcel GB5, customs GB6, dangerous
/// goods GC9): each adapter against doc-derived (Sendcloud, PostNL) or SDK-derived (bpost) answers. No network: a fake
/// handler serves the fixtures and records what the adapter sent.
/// </summary>
public class CarrierAdapterTests
{
    private static readonly ShippingAddress Shop = new() { Name = "Shop", Company = "Shop BV", Street = "Siriusdreef", HouseNumber = "42", PostalCode = "2132WT", City = "Hoofddorp", Country = "NL" };

    private static CarrierShipmentRequest Request(string service, int parcels = 1, ShippingAddress? to = null, CustomsDeclaration? customs = null) => new()
    {
        Reference = "orders/12345",
        ShipmentId = "Shipments/orders/12345",
        ServiceCode = service,
        ShipFrom = Shop,
        ShipTo = to ?? new ShippingAddress { Name = "John Doe", Street = "Stadhuisplein", HouseNumber = "10", PostalCode = "5611 EM", City = "Eindhoven", Country = "NL", Email = "john@example.org" },
        Parcels = Enumerable.Range(1, parcels).Select(i => new ParcelSpec(1000 * i)).ToList(),
        Customs = customs,
    };

    private static CustomsDeclaration TShirts() => new()
    {
        Document = CustomsDocumentKind.Cn23,
        ContentType = CustomsContentType.CommercialGoods,
        InvoiceNumber = "22334455",
        Items = [new CustomsItem { Description = "Cotton T-shirt", Quantity = 2, WeightGrams = 400, Value = new Money(40m, "EUR"), HsCode = "610910", OriginCountry = "PT", Sku = "TS-1" }],
    };

    private static readonly ShippingAddress Shanghai = new() { Name = "Peter de Ruiter", Street = "Nanjinglu", HouseNumber = "137", PostalCode = "310000", City = "Shanghai", Country = "CN" };

    // ---- Sendcloud -------------------------------------------------------------------------------------------------

    private static (SendcloudCarrier Carrier, FakeProviderHandler Handler) Sendcloud(SendcloudOptions? options = null)
    {
        var handler = new FakeProviderHandler();
        var settings = options ?? new SendcloudOptions { PublicKey = "public_placeholder", SecretKey = "secret_placeholder" };
        settings.Services["8"] = new CarrierServiceOptions { Name = "PostNL Standard 0-23kg", Countries = ["NL", "BE"] };
        return (new SendcloudCarrier(new SendcloudApi(new FakeHttpClientFactory(handler), new StaticOptionsMonitor<SendcloudOptions>(settings))), handler);
    }

    [Fact]
    public void Sendcloud_books_one_batch_entry_per_parcel_with_customs_lines_and_the_service_point()
    {
        var request = Request("8", parcels: 2, to: Shanghai, customs: TShirts()) with { ServicePointId = "217165" };
        var body = SendcloudCarrier.BuildRequest(request, new SendcloudOptions());

        var parcels = body["parcels"]!.AsArray();
        parcels.Count.Should().Be(2);
        var first = parcels[0]!.AsObject();
        first["shipment"]!["id"]!.GetValue<int>().Should().Be(8);
        first["request_label"]!.GetValue<bool>().Should().BeTrue();
        first["weight"]!.GetValue<string>().Should().Be("1.000");
        parcels[1]!["weight"]!.GetValue<string>().Should().Be("2.000");
        first["to_service_point"]!.GetValue<int>().Should().Be(217165);
        first["external_reference"]!.GetValue<string>().Should().Be("Shipments/orders/12345/1");
        first["customs_information"]!["customs_shipment_type"]!.GetValue<int>().Should().Be(2);
        var item = first["parcel_items"]!.AsArray().Single()!;
        item["hs_code"]!.GetValue<string>().Should().Be("610910");
        item["origin_country"]!.GetValue<string>().Should().Be("PT");
        // Per single item, as the schema states: 2 × 200 g at 20.00.
        item["weight"]!.GetValue<string>().Should().Be("0.200");
        item["value"]!.GetValue<string>().Should().Be("20.00");
    }

    [Fact]
    public void Sendcloud_return_parcels_carry_the_buyers_address_and_is_return()
    {
        var request = Request("8") with { IsReturn = true, ShipFrom = new ShippingAddress { Name = "Buyer", Street = "Damrak", HouseNumber = "1", PostalCode = "1012AB", City = "Amsterdam", Country = "NL" }, ShipTo = Shop };
        var parcel = SendcloudCarrier.BuildRequest(request, new SendcloudOptions { SenderAddressId = 9 })["parcels"]![0]!;
        parcel["is_return"]!.GetValue<bool>().Should().BeTrue();
        parcel["name"]!.GetValue<string>().Should().Be("Buyer");
        (parcel["sender_address"] is null).Should().BeTrue();
    }

    [Fact]
    public async Task Sendcloud_reads_each_parcel_downloads_its_label_and_the_CN23()
    {
        var (carrier, handler) = Sendcloud();
        handler.On("POST", "/api/v2/parcels", CarrierFixtures.Text("Sendcloud", "parcels-created.json"));
        handler.On("GET", "/api/v2/label/label_printer/", "%PDF-1.4 label");
        handler.On("GET", "/api/v2/parcels/3/documents/cn23-default", "%PDF-1.4 cn23");

        var result = await carrier.CreateShipmentAsync(Request("8", parcels: 2, to: Shanghai, customs: TShirts()), default);

        result.Parcels.Select(p => p.CarrierParcelId).Should().Equal(["3", "4"]);
        result.Parcels[0].TrackingNumber.Should().Be("S0M3TR4Ck1NgNumB3r");
        result.Parcels[0].TrackingUrl.Should().StartWith("https://tracking.sendcloud.sc/");
        result.CustomsDocument.Should().NotBeNull();
        handler.Requests.First().Headers["Authorization"].Should().StartWith("Basic ");
        handler.Requests.Count(r => r.Uri.AbsolutePath.StartsWith("/api/v2/label/label_printer/", StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public async Task Sendcloud_failed_parcels_throw_with_the_carriers_errors()
    {
        var (carrier, handler) = Sendcloud();
        handler.On("POST", "/api/v2/parcels", CarrierFixtures.Text("Sendcloud", "failed-parcels.json"));
        var act = async () => await carrier.CreateShipmentAsync(Request("8"), default);
        await act.Should().ThrowAsync<CarrierException>();
    }

    [Fact]
    public void Sendcloud_never_sends_credentials_outside_its_hosts()
    {
        var options = new SendcloudOptions();
        SendcloudApi.IsSendcloudUrl("https://panel.sendcloud.sc/api/v2/label/label_printer/3?hash=x", options).Should().BeTrue();
        SendcloudApi.IsSendcloudUrl("https://servicepoints.sendcloud.sc/api/v2/service-points?country=NL", options).Should().BeTrue();
        SendcloudApi.IsSendcloudUrl("https://evil.example/api/v2/label", options).Should().BeFalse();
        SendcloudApi.IsSendcloudUrl("file:///etc/passwd", options).Should().BeFalse();
    }

    [Fact]
    public void Sendcloud_webhooks_verify_with_the_documented_test_vector()
    {
        // sendcloud.dev/api/v2/webhooks: body {"key": "value"}, secret "secretkey".
        var strategy = SendcloudShippingExtensions.Verification(new SendcloudOptions { SecretKey = "other", Accounts = { ["seller"] = new SendcloudAccount { SecretKey = "secretkey" } } });
        var body = Encoding.UTF8.GetBytes("{\"key\": \"value\"}");
        WebhookRequest Signed(string signature) => new([new("Sendcloud-Signature", new StringValues(signature))], body);

        strategy.Verify(Signed("1eed4b3d41f4653ac64fd56f1bf1cbfd349e4482cbc11dff7134bd93e5da4b0a")).IsAccepted.Should().BeTrue();
        strategy.Verify(Signed("0eed4b3d41f4653ac64fd56f1bf1cbfd349e4482cbc11dff7134bd93e5da4b0a")).IsAccepted.Should().BeFalse();
        SendcloudShippingExtensions.Verification(new SendcloudOptions()).Verify(Signed("1eed4b3d41f4653ac64fd56f1bf1cbfd349e4482cbc11dff7134bd93e5da4b0a")).IsAccepted.Should().BeFalse();
    }

    [Fact]
    public void Sendcloud_status_webhooks_become_events_with_a_stable_id_and_the_carriers_time()
    {
        var root = CarrierFixtures.Json("Sendcloud", "webhook-parcel-status-changed.json");
        var first = SendcloudCarrier.ParseWebhook(root).Single();
        var again = SendcloudCarrier.ParseWebhook(root).Single();

        first.TrackingNumber.Should().Be("3SYZXG132912330");
        first.CarrierParcelId.Should().Be("1");
        first.Status.Should().Be(TrackingStatus.Delivered);
        first.OccurredAt.Should().Be(DateTimeOffset.Parse("2026-10-22T09:00:00Z"));
        again.EventId.Should().Be(first.EventId);

        var other = JsonNode.Parse(root.GetRawText())!;
        other["action"] = "integration_updated";
        using var ignored = JsonDocument.Parse(other.ToJsonString());
        SendcloudCarrier.ParseWebhook(ignored.RootElement).Should().BeEmpty();
    }

    [Theory]
    [InlineData("11", null, TrackingStatus.Delivered)]
    [InlineData("2000", null, TrackingStatus.Cancelled)]
    [InlineData("1000", null, TrackingStatus.Labelled)]
    [InlineData("3", "En route to sorting center", TrackingStatus.InTransit)]
    [InlineData("12", "Awaiting customer pickup", TrackingStatus.AvailableForPickup)]
    [InlineData("80", "Unable to deliver", TrackingStatus.Exception)]
    [InlineData("999", "Parcel en route", TrackingStatus.InTransit)]
    public void Sendcloud_statuses_map_by_id_then_by_message(string id, string? message, TrackingStatus expected)
        => SendcloudCarrier.MapStatus(id, message).Should().Be(expected);

    [Fact]
    public async Task Sendcloud_service_points_read_lockers_hours_and_skip_inactive_points()
    {
        var (carrier, handler) = Sendcloud();
        handler.On("GET", "/api/v2/service-points", CarrierFixtures.Text("Sendcloud", "service-points.json"));
        var points = await carrier.SearchAsync(new ServicePointQuery { Country = "NL", PostalCode = "5611AG", Limit = 10 }, default);

        points.Should().HaveCount(2);
        points[0].Id.Should().Be("10875349");
        points[0].Kind.Should().Be(ServicePointKind.PickupPoint);
        points[0].Latitude.Should().Be(51.441444);
        points[0].DistanceMetres.Should().Be(381);
        points[0].OpeningHours["monday"].Should().Equal(["10:00-20:00"]);
        points[1].Kind.Should().Be(ServicePointKind.ParcelLocker);
        handler.Requests.Single().Uri.Query.Should().Contain("postal_code=5611AG");
    }

    // ---- PostNL ----------------------------------------------------------------------------------------------------

    private static readonly PostNLCarrier.PostNLCredentials PostNLAccount = new("apikey_placeholder", "DEVC", "11223344", "123456");

    [Fact]
    public void PostNL_multi_collo_shipments_share_a_main_barcode_in_type_03_groups()
    {
        var body = PostNLCarrier.BuildRequest(Request("3085", parcels: 2), new PostNLOptions(), PostNLAccount,
            ["3SDEVC000000001", "3SDEVC000000002"], DateTimeOffset.Parse("2026-10-21T12:31:21Z"));

        body["Customer"]!["CustomerCode"]!.GetValue<string>().Should().Be("DEVC");
        body["Customer"]!["Address"]!["AddressType"]!.GetValue<string>().Should().Be("02");
        // Dutch wall-clock time (CEST, UTC+2), in the documented format.
        body["Message"]!["MessageTimeStamp"]!.GetValue<string>().Should().Be("21-10-2026 14:31:21");
        body["Message"]!["Printertype"]!.GetValue<string>().Should().Be("GraphicFile|PDF");
        var shipments = body["Shipments"]!.AsArray();
        shipments.Count.Should().Be(2);
        var second = shipments[1]!;
        second["Barcode"]!.GetValue<string>().Should().Be("3SDEVC000000002");
        var group = second["Groups"]![0]!;
        group["GroupType"]!.GetValue<string>().Should().Be("03");
        group["GroupSequence"]!.GetValue<int>().Should().Be(2);
        group["GroupCount"]!.GetValue<int>().Should().Be(2);
        group["MainBarcode"]!.GetValue<string>().Should().Be("3SDEVC000000001");
        second["Dimension"]!["Weight"]!.GetValue<string>().Should().Be("2000");
        second["Addresses"]![0]!["Zipcode"]!.GetValue<string>().Should().Be("5611EM");
        (second["Customs"] is null).Should().BeTrue();
    }

    [Fact]
    public void PostNL_non_EU_shipments_carry_customs_content_and_dangerous_goods_the_ADR_reference()
    {
        var request = Request("4945", to: Shanghai, customs: TShirts()) with { DangerousGoods = [new DangerousGoodsItem { Class = DangerousGoodsClass.LimitedQuantity }] };
        var shipment = PostNLCarrier.BuildRequest(request, new PostNLOptions(), PostNLAccount, null, DateTimeOffset.UnixEpoch)["Shipments"]![0]!;

        (shipment["Barcode"] is null).Should().BeTrue();
        shipment["Reference"]!.GetValue<string>().Should().Be("ADR/LQ orders/12345");
        var customs = shipment["Customs"]!;
        customs["ShipmentType"]!.GetValue<string>().Should().Be("Commercial Goods");
        customs["Currency"]!.GetValue<string>().Should().Be("EUR");
        customs["InvoiceNr"]!.GetValue<string>().Should().Be("22334455");
        var content = customs["Content"]![0]!;
        content["HSTariffNr"]!.GetValue<string>().Should().Be("610910");
        content["CountryOfOrigin"]!.GetValue<string>().Should().Be("PT");
        content["Weight"]!.GetValue<int>().Should().Be(400);
    }

    [Fact]
    public void PostNL_delivery_to_a_location_adds_its_address_as_type_09()
    {
        var point = new ShipmentServicePoint { Id = "163043", Name = "Tonys Tabakszaak", Address = new ShippingAddress { Street = "Biltstraat", HouseNumber = "12", PostalCode = "3572AE", City = "Utrecht", Country = "NL" } };
        var shipment = PostNLCarrier.BuildRequest(Request("3533") with { ServicePointId = point.Id, ServicePoint = point }, new PostNLOptions(), PostNLAccount, null, DateTimeOffset.UnixEpoch)["Shipments"]![0]!;
        shipment["DeliveryAddress"]!.GetValue<string>().Should().Be("09");
        shipment["Addresses"]!.AsArray().Select(a => a!["AddressType"]!.GetValue<string>()).Should().Equal(["01", "09"]);
    }

    [Fact]
    public void PostNL_labels_and_the_CN23_are_read_per_shipment()
    {
        var result = PostNLCarrier.ReadResponse(CarrierFixtures.Json("PostNL", "label-response.json"), 2);
        result.Parcels.Select(p => p.TrackingNumber).Should().Equal(["3SDEVC272730803", "3SDEVC272730804"]);
        result.Parcels[0].Label.MediaType.Should().Be(CarrierDocument.Pdf);
        Encoding.ASCII.GetString(result.Parcels[0].Label.Content).Should().StartWith("%PDF");
        result.CustomsDocument.Should().NotBeNull();
        Action tooFew = () => PostNLCarrier.ReadResponse(CarrierFixtures.Json("PostNL", "label-response.json"), 3);
        tooFew.Should().Throw<CarrierException>();
    }

    [Fact]
    public void PostNL_status_reads_the_code_and_Dutch_time()
    {
        var events = PostNLCarrier.ReadStatus(CarrierFixtures.Json("PostNL", "status-current.json"));
        var status = events.Single();
        status.TrackingNumber.Should().Be("3SDEVC288882229");
        status.Status.Should().Be(TrackingStatus.OutForDelivery);
        status.CarrierStatus.Should().Be("7");
        // 08-11-2022 10:13:20 in Amsterdam (CET, UTC+1).
        status.OccurredAt.Should().Be(DateTimeOffset.Parse("2022-11-08T09:13:20Z"));
        PostNLCarrier.ReadStatus(CarrierFixtures.Json("PostNL", "status-current.json")).Single().EventId.Should().Be(status.EventId);
    }

    [Theory]
    [InlineData("1", TrackingStatus.Labelled)]
    [InlineData("5", TrackingStatus.InTransit)]
    [InlineData("7", TrackingStatus.OutForDelivery)]
    [InlineData("8", TrackingStatus.Exception)]
    [InlineData("11", TrackingStatus.Delivered)]
    [InlineData("12", TrackingStatus.AvailableForPickup)]
    [InlineData("99", TrackingStatus.InTransit)]
    public void PostNL_status_codes_map_by_phase(string code, TrackingStatus expected)
        => PostNLCarrier.MapStatus(code).Should().Be(expected);

    [Fact]
    public void PostNL_locations_read_lockers_and_opening_hours()
    {
        var points = PostNLCarrier.ReadLocations(CarrierFixtures.Json("PostNL", "locations.json")).ToList();
        points.Should().HaveCount(2);
        points[0].Id.Should().Be("163043");
        points[0].Address.HouseNumber.Should().Be("12");
        points[0].Address.Addition.Should().BeNull();
        points[0].OpeningHours["monday"].Should().Equal(["08:00-18:00"]);
        points[0].OpeningHours["saturday"].Should().HaveCount(2);
        points[0].Kind.Should().Be(ServicePointKind.PickupPoint);
        points[1].Kind.Should().Be(ServicePointKind.ParcelLocker);
    }

    [Fact]
    public async Task PostNL_fetches_main_barcodes_before_a_multi_collo_label()
    {
        var handler = new FakeProviderHandler()
            .On("GET", "/shipment/v1_1/barcode", CarrierFixtures.Text("PostNL", "barcode.json"))
            .On("POST", "/shipment/v2_2/label", CarrierFixtures.Text("PostNL", "label-response.json"));
        var options = new PostNLOptions { ApiKey = "apikey_placeholder", CustomerCode = "DEVC", CustomerNumber = "11223344" };
        var carrier = new PostNLCarrier(new FakeHttpClientFactory(handler), new StaticOptionsMonitor<PostNLOptions>(options), new ManualTimeProvider(DateTimeOffset.Parse("2026-10-21T10:00:00Z")));

        await carrier.CreateShipmentAsync(Request("3085", parcels: 2), default);

        handler.Requests.Count(r => r.Uri.AbsolutePath == "/shipment/v1_1/barcode").Should().Be(2);
        var label = handler.Requests.Single(r => r.Method == "POST");
        label.Uri.Query.Should().Contain("confirm=true");
        label.Headers["apikey"].Should().Be("apikey_placeholder");
    }

    // ---- bpost -----------------------------------------------------------------------------------------------------

    private static readonly ShippingAddress Brussels = new() { Name = "Shop", Street = "Munt", HouseNumber = "1", PostalCode = "1000", City = "Brussel", Country = "BE" };

    [Fact]
    public void Bpost_orders_have_one_box_per_parcel_at_home()
    {
        var request = Request("bpack 24h Pro", parcels: 2) with
        {
            ShipFrom = Brussels,
            ShipTo = new ShippingAddress { Name = "Buyer", Street = "Kerkstraat", HouseNumber = "5", PostalCode = "9000", City = "Gent", Country = "BE" },
        };
        var order = BpostCarrier.BuildOrder(request, "120865", BpostCarrier.OrderReference(request.ShipmentId), null).Root!;
        XNamespace tns = "http://schema.post.be/shm/deepintegration/v5/";
        XNamespace national = "http://schema.post.be/shm/deepintegration/v5/national";

        order.Element(tns + "reference")!.Value.Should().Be("Shipments-orders-12345");
        var boxes = order.Elements(tns + "box").ToList();
        boxes.Should().HaveCount(2);
        var atHome = boxes[1].Element(tns + "nationalBox")!.Element(national + "atHome")!;
        atHome.Element(national + "product")!.Value.Should().Be("bpack 24h Pro");
        atHome.Element(national + "weight")!.Value.Should().Be("2000");
    }

    [Fact]
    public void Bpost_pick_up_points_and_lockers_name_the_point()
    {
        var point = new ShipmentServicePoint { Id = "100472", Name = "RELAY CENTRAAL STATION", Address = new ShippingAddress { Street = "EUROPAKRUISPUNT", HouseNumber = "2", PostalCode = "1000", City = "BRUSSEL", Country = "BE" } };
        var request = Request("bpack@bpost") with { ShipFrom = Brussels, ShipTo = Brussels, ServicePointId = point.Id, ServicePoint = point };
        XNamespace national = "http://schema.post.be/shm/deepintegration/v5/national";

        var atBpost = BpostCarrier.BuildOrder(request, "1", "r", null).Descendants(national + "atBpost").Single();
        atBpost.Element(national + "pugoId")!.Value.Should().Be("100472");
        atBpost.Element(national + "receiverName")!.Value.Should().Be("Shop");

        var locker = BpostCarrier.BuildOrder(request with { ServiceCode = "bpack 24/7" }, "1", "r", null).Descendants(national + "at247").Single();
        locker.Element(national + "parcelsDepotId")!.Value.Should().Be("100472");
    }

    [Fact]
    public void Bpost_international_boxes_carry_customs_lines_with_HS_code_and_origin()
    {
        var order = BpostCarrier.BuildOrder(Request("bpack World Express Pro", to: Shanghai, customs: TShirts()) with { ShipFrom = Brussels }, "1", "r", null);
        XNamespace international = "http://schema.post.be/shm/deepintegration/v5/international";
        var info = order.Descendants(international + "customsInfo").Single();
        info.Element(international + "parcelValue")!.Value.Should().Be("4000");
        info.Element(international + "shipmentType")!.Value.Should().Be("GOODS");
        var content = order.Descendants(international + "parcelContent").Single();
        content.Element(international + "hsTariffCode")!.Value.Should().Be("610910");
        content.Element(international + "originOfGoods")!.Value.Should().Be("PT");
        content.Element(international + "nettoWeight")!.Value.Should().Be("400");
    }

    [Fact]
    public void Bpost_labels_map_every_box_barcode_even_in_one_order_PDF()
    {
        var result = BpostCarrier.ReadLabels(CarrierFixtures.Xml("Bpost", "labels.xml"), "my-reference", 2);
        result.Parcels.Select(p => p.TrackingNumber).Should().Equal(["323299901059912015292030", "323299901059912015293050"]);
        result.Parcels[0].CarrierParcelId.Should().Be("my-reference:323299901059912015292030");
        result.Parcels[0].Label.MediaType.Should().Be(CarrierDocument.Pdf);
    }

    [Fact]
    public void Bpost_box_status_becomes_a_tracking_event_deduplicated_on_box_and_status()
    {
        var now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
        var events = BpostCarrier.ReadOrderStatus(CarrierFixtures.Xml("Bpost", "order-fetched.xml"), now);
        var status = events.Single();
        status.TrackingNumber.Should().Be("323212086559959097180037");
        status.Status.Should().Be(TrackingStatus.AvailableForPickup);
        BpostCarrier.ReadOrderStatus(CarrierFixtures.Xml("Bpost", "order-fetched.xml"), now.AddHours(1)).Single().EventId.Should().Be(status.EventId);
    }

    [Fact]
    public void Bpost_locator_points_and_lockers()
    {
        var points = BpostCarrier.ReadLocator(CarrierFixtures.Xml("Bpost", "locator.xml")).ToList();
        points.Should().HaveCount(2);
        points[0].Id.Should().Be("100472");
        points[0].Address.PostalCode.Should().Be("1000");
        points[0].DistanceMetres.Should().Be(336);
        points[0].Kind.Should().Be(ServicePointKind.PickupPoint);
        points[1].Kind.Should().Be(ServicePointKind.ParcelLocker);
    }

    [Fact]
    public void No_adapter_ships_a_service_that_accepts_dangerous_goods_until_the_host_says_so()
    {
        PostNLCarrier.Shipped.All(s => s.AcceptedDangerousGoods.Count == 0).Should().BeTrue();
        BpostCarrier.Shipped.All(s => s.AcceptedDangerousGoods.Count == 0).Should().BeTrue();

        var configured = CarrierCatalog.Apply(PostNLCarrier.Shipped, new Dictionary<string, CarrierServiceOptions>
        {
            ["3085"] = new() { AcceptedDangerousGoods = [DangerousGoodsClass.LithiumIonInEquipment] },
            ["4945"] = new() { Enabled = false },
            ["3089"] = new() { Name = "PostNL signature", Countries = ["NL"] },
        });
        configured.Single(s => s.Code == "3085").AcceptedDangerousGoods.Should().Equal([DangerousGoodsClass.LithiumIonInEquipment]);
        configured.Any(s => s.Code == "4945").Should().BeFalse();
        configured.Single(s => s.Code == "3089").Countries.Should().Equal(["NL"]);
    }
}
