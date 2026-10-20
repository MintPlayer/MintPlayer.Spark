using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Primitives;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Shipping;
using MintPlayer.Spark.Commerce.Shipping.Bpost;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Commerce.Shipping.Sendcloud;
using MintPlayer.Spark.Commerce.Tests.Payments;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Tests.Shipping;

/// <summary>A fake carrier host: routes by method and path prefix (longest wins), each with its status, body and media type.</summary>
internal sealed class CarrierFakeHandler : HttpMessageHandler
{
    private readonly List<(string Method, string Prefix, HttpStatusCode Status, string Body, string MediaType)> routes = [];

    public List<RecordedRequest> Requests { get; } = [];

    public CarrierFakeHandler On(string method, string pathPrefix, string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json")
    {
        routes.Add((method, pathPrefix, status, body, mediaType));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri!, headers, body));
        var path = request.RequestUri!.PathAndQuery;
        var route = routes.Where(r => r.Method == request.Method.Method && path.StartsWith(r.Prefix, StringComparison.Ordinal))
            .OrderByDescending(r => r.Prefix.Length)
            .FirstOrDefault();
        if (route.Body is null)
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"errors\":[{\"status\":\"404\",\"code\":\"not_found\",\"detail\":\"Not found\"}]}", Encoding.UTF8, "application/json") };
        return new HttpResponseMessage(route.Status) { Content = new StringContent(route.Body, Encoding.UTF8, route.MediaType) };
    }
}

/// <summary>
/// Sendcloud on API v3 (#521 item 11: v2 parcel creation is closed to accounts created on or after 2026-04-13), against
/// fixtures derived from sendcloud.dev's v3 reference (Shipping/Fixtures/Sendcloud, each marked with its source), and
/// the booking lookups of Sendcloud and bpost. No network.
/// </summary>
public class SendcloudAdapterTests
{
    private static readonly ShippingAddress Shop = new() { Name = "Shop", Company = "Shop BV", Street = "Siriusdreef", HouseNumber = "42", PostalCode = "2132WT", City = "Hoofddorp", Country = "NL" };

    private static readonly ShippingAddress Shanghai = new() { Name = "Peter de Ruiter", Street = "Nanjinglu", HouseNumber = "137", PostalCode = "310000", City = "Shanghai", Country = "CN" };

    private static CarrierShipmentRequest Request(string service = "postnl:standard", int parcels = 1, ShippingAddress? to = null, CustomsDeclaration? customs = null) => new()
    {
        Reference = "orders/12345",
        ShipmentId = "Shipments/orders/12345",
        ServiceCode = service,
        ShipFrom = Shop,
        ShipTo = to ?? new ShippingAddress { Name = "John Doe", Street = "Stadhuisplein", HouseNumber = "10", PostalCode = "5611 EM", City = "Eindhoven", Country = "NL", Email = "john@example.org" },
        Parcels = Enumerable.Range(1, parcels).Select(i => new ParcelSpec(1000 * i, 300, 200, 100)).ToList(),
        Customs = customs,
    };

    private static CustomsDeclaration TShirts() => new()
    {
        Document = CustomsDocumentKind.Cn23,
        ContentType = CustomsContentType.CommercialGoods,
        InvoiceNumber = "22334455",
        Items = [new CustomsItem { Description = "Cotton T-shirt", Quantity = 2, WeightGrams = 400, Value = new Money(40m, "EUR"), HsCode = "610910", OriginCountry = "PT", Sku = "TS-1" }],
    };

    private static (SendcloudCarrier Carrier, CarrierFakeHandler Handler) Sendcloud(SendcloudOptions? options = null)
    {
        var handler = new CarrierFakeHandler();
        var settings = options ?? new SendcloudOptions { PublicKey = "public_placeholder", SecretKey = "secret_placeholder" };
        settings.Services["postnl:standard"] = new CarrierServiceOptions { Name = "PostNL Standard", Countries = ["NL", "BE", "CN"] };
        return (new SendcloudCarrier(new SendcloudApi(new FakeHttpClientFactory(handler), new StaticOptionsMonitor<SendcloudOptions>(settings))), handler);
    }

    // ---- Request mapping -------------------------------------------------------------------------------------------

    [Fact]
    public void A_shipment_is_one_v3_announce_with_a_parcel_per_parcel_customs_once_and_our_key_as_external_reference()
    {
        var options = new SendcloudOptions { ContractIds = { ["postnl:standard"] = 517 } };
        var body = SendcloudCarrier.BuildShipment(Request(parcels: 2, to: Shanghai, customs: TShirts()) with { ServicePointId = "217165" }, options);

        body["ship_with"]!["type"]!.GetValue<string>().Should().Be("shipping_option_code");
        body["ship_with"]!["properties"]!["shipping_option_code"]!.GetValue<string>().Should().Be("postnl:standard");
        body["ship_with"]!["properties"]!["contract_id"]!.GetValue<long>().Should().Be(517);
        body["external_reference_id"]!.GetValue<string>().Should().Be("Shipments/orders/12345");
        body["order_number"]!.GetValue<string>().Should().Be("orders/12345");
        body["to_address"]!["address_line_1"]!.GetValue<string>().Should().Be("Nanjinglu");
        body["to_address"]!["country_code"]!.GetValue<string>().Should().Be("CN");
        body["from_address"]!["company_name"]!.GetValue<string>().Should().Be("Shop BV");
        body["to_service_point"]!["id"]!.GetValue<string>().Should().Be("217165");

        var parcels = body["parcels"]!.AsArray();
        parcels.Count.Should().Be(2);
        parcels[0]!["weight"]!["value"]!.GetValue<string>().Should().Be("1.000");
        parcels[0]!["weight"]!["unit"]!.GetValue<string>().Should().Be("kg");
        parcels[1]!["weight"]!["value"]!.GetValue<string>().Should().Be("2.000");
        parcels[0]!["dimensions"]!["length"]!.GetValue<string>().Should().Be("30.0");
        parcels[0]!["dimensions"]!["unit"]!.GetValue<string>().Should().Be("cm");

        // The declaration's lines travel once: 2 × 200 g at 20.00 each, per single item as the schema states.
        var item = parcels[0]!["parcel_items"]!.AsArray().Single()!;
        item["hs_code"]!.GetValue<string>().Should().Be("610910");
        item["origin_country"]!.GetValue<string>().Should().Be("PT");
        item["weight"]!["value"]!.GetValue<decimal>().Should().Be(0.2m);
        item["price"]!["value"]!.GetValue<string>().Should().Be("20.00");
        item["price"]!["currency"]!.GetValue<string>().Should().Be("EUR");
        (parcels[1]!["parcel_items"] is null).Should().BeTrue();
        body["customs_information"]!["invoice_number"]!.GetValue<string>().Should().Be("22334455");
        body["customs_information"]!["export_reason"]!.GetValue<string>().Should().Be("commercial_goods");
        body["total_order_price"]!["value"]!.GetValue<string>().Should().Be("40.00");
        body["label_details"]!["mime_type"]!.GetValue<string>().Should().Be("application/pdf");
    }

    [Fact]
    public void A_configured_sender_address_replaces_the_ship_from_and_zpl_labels_state_their_dpi()
    {
        var body = SendcloudCarrier.BuildShipment(Request() with { LabelFormat = LabelFormat.Zpl }, new SendcloudOptions { SenderAddressId = 9, ZplDpi = 300 });
        body["from_address"]!["sender_address_id"]!.GetValue<long>().Should().Be(9);
        (body["from_address"]!["name"] is null).Should().BeTrue();
        body["label_details"]!["mime_type"]!.GetValue<string>().Should().Be("application/zpl");
        body["label_details"]!["dpi"]!.GetValue<int>().Should().Be(300);
    }

    [Fact]
    public void Service_codes_must_be_shipping_option_codes_and_a_shipment_holds_at_most_15_parcels()
    {
        var numeric = () => SendcloudCarrier.BuildShipment(Request(service: "8"), new SendcloudOptions());
        numeric.Should().Throw<CarrierException>().WithMessage("*shipping_option_code*");
        var many = () => SendcloudCarrier.BuildShipment(Request(parcels: 16), new SendcloudOptions());
        many.Should().Throw<CarrierException>();
    }

    [Fact]
    public void A_return_goes_to_the_returns_API_from_the_buyer_with_returned_goods_customs()
    {
        var buyer = new ShippingAddress { Name = "Buyer", Street = "Nanjinglu", HouseNumber = "1", PostalCode = "310000", City = "Shanghai", Country = "CN" };
        var request = Request(service: "postnl:return", customs: TShirts()) with { IsReturn = true, ShipFrom = buyer, ShipTo = Shop };
        var body = SendcloudCarrier.BuildReturn(request, new SendcloudOptions());

        body["from_address"]!["name"]!.GetValue<string>().Should().Be("Buyer");
        body["to_address"]!["name"]!.GetValue<string>().Should().Be("Shop");
        body["ship_with"]!["shipping_option_code"]!.GetValue<string>().Should().Be("postnl:return");
        body["weight"]!["value"]!.GetValue<string>().Should().Be("1.000");
        body["external_reference"]!.GetValue<string>().Should().Be("Shipments/orders/12345");
        body["customs_information"]!["export_reason"]!.GetValue<string>().Should().Be("returned_goods");
        body["customs_invoice_nr"]!.GetValue<string>().Should().Be("22334455");
        ((IEnumerable<System.Text.Json.Nodes.JsonNode?>)body["parcel_items"]!.AsArray()).Should().ContainSingle();

        var twoParcels = () => SendcloudCarrier.BuildReturn(request with { Parcels = [new ParcelSpec(500), new ParcelSpec(600)] }, new SendcloudOptions());
        twoParcels.Should().Throw<CarrierException>();
    }

    // ---- Booking over HTTP ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Booking_reads_each_parcel_and_downloads_its_label_and_the_customs_declaration()
    {
        var (carrier, handler) = Sendcloud();
        handler.On("POST", "/api/v3/shipments/announce", CarrierFixtures.Text("Sendcloud", "shipment-announced.json"), HttpStatusCode.Created);
        handler.On("GET", "/api/v3/parcels/383707309/documents/label", "%PDF-1.4 label 1", mediaType: "application/pdf");
        handler.On("GET", "/api/v3/parcels/383707310/documents/label", "%PDF-1.4 label 2", mediaType: "application/pdf");
        handler.On("GET", "/api/v3/parcels/383707309/documents/customs-declaration", "%PDF-1.4 cn23", mediaType: "application/pdf");

        var result = await carrier.CreateShipmentAsync(Request(parcels: 2, to: Shanghai, customs: TShirts()), default);

        result.CarrierShipmentId.Should().Be("XXX-Shipment-id");
        result.Parcels.Select(p => p.CarrierParcelId).Should().Equal(["383707309", "383707310"]);
        result.Parcels[0].TrackingNumber.Should().Be("3SYZXG8498635");
        result.Parcels[0].Label.MediaType.Should().Be(CarrierDocument.Pdf);
        Encoding.ASCII.GetString(result.Parcels[1].Label.Content).Should().Be("%PDF-1.4 label 2");
        result.CustomsDocument!.MediaType.Should().Be(CarrierDocument.Pdf);
        handler.Requests.First().Headers["Authorization"].Should().StartWith("Basic ");
        handler.Requests.First().Uri.AbsoluteUri.Should().Be("https://panel.sendcloud.sc/api/v3/shipments/announce");
        handler.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/documents/label", StringComparison.Ordinal))
            .Should().OnlyContain(r => r.Headers["Accept"] == "application/pdf" && r.Uri.Query.Contains("paper_size=A6"));
    }

    [Fact]
    public async Task A_repeated_announce_answers_409_with_the_shipment_Sendcloud_already_holds()
    {
        var (carrier, handler) = Sendcloud();
        // The 409 answer is the sync-shipment-response schema, like the 201: the existing shipment.
        handler.On("POST", "/api/v3/shipments/announce", CarrierFixtures.Text("Sendcloud", "shipment-announced.json"), HttpStatusCode.Conflict);
        handler.On("GET", "/api/v3/parcels/383707309/documents/label", "%PDF-1.4 label 1", mediaType: "application/pdf");
        handler.On("GET", "/api/v3/parcels/383707310/documents/label", "%PDF-1.4 label 2", mediaType: "application/pdf");

        var result = await carrier.CreateShipmentAsync(Request(parcels: 2), default);
        result.Parcels.Select(p => p.TrackingNumber).Should().Equal(["3SYZXG8498635", "3SYZXG8498636"]);
        handler.Requests.Count(r => r.Method == "POST").Should().Be(1);
    }

    [Fact]
    public async Task A_failed_announcement_is_a_refusal_with_the_carriers_message()
    {
        var (carrier, handler) = Sendcloud();
        handler.On("POST", "/api/v3/shipments/announce", CarrierFixtures.Text("Sendcloud", "shipment-announcement-failed.json"), HttpStatusCode.Created);
        var act = async () => await carrier.CreateShipmentAsync(Request(), default);
        var thrown = await act.Should().ThrowAsync<CarrierException>();
        thrown.Which.StatusCode.Should().Be(422);
        thrown.Which.Message.Should().Contain("postal code is invalid");
    }

    [Fact]
    public async Task A_validation_error_names_the_field_and_is_a_refusal()
    {
        var (carrier, handler) = Sendcloud();
        handler.On("POST", "/api/v3/shipments/announce", CarrierFixtures.Text("Sendcloud", "error-required.json"), HttpStatusCode.BadRequest);
        var act = async () => await carrier.CreateShipmentAsync(Request(), default);
        var thrown = await act.Should().ThrowAsync<CarrierException>();
        thrown.Which.StatusCode.Should().Be(400);
        thrown.Which.Message.Should().Contain("/to_address/name").And.Contain("This field is required.");
    }

    [Fact]
    public async Task A_return_label_is_announced_then_read_and_its_label_downloaded()
    {
        var (carrier, handler) = Sendcloud();
        handler.On("POST", "/api/v3/returns/announce-synchronously", CarrierFixtures.Text("Sendcloud", "return-announced.json"), HttpStatusCode.Created);
        handler.On("GET", "/api/v3/returns/12345", CarrierFixtures.Text("Sendcloud", "return.json"));
        handler.On("GET", "/api/v3/parcels/67880/documents/label", "%PDF-1.4 return", mediaType: "application/pdf");

        var result = await carrier.CreateShipmentAsync(Request(service: "postnl:return") with { IsReturn = true }, default);
        result.CarrierShipmentId.Should().Be("return:12345");
        result.Parcels.Single().CarrierParcelId.Should().Be("67880");
        result.Parcels.Single().TrackingNumber.Should().Be("3SRETURN0067880");
    }

    // ---- Lookup, void, tracking, service points ----------------------------------------------------------------

    [Fact]
    public async Task The_lookup_finds_a_booking_by_its_external_reference_and_answers_not_found_otherwise()
    {
        var (carrier, handler) = Sendcloud();
        handler.On("GET", "/api/v3/shipments?external_reference_id=Shipments%2Forders%2F12345", CarrierFixtures.Text("Sendcloud", "shipments-by-reference.json"));
        handler.On("GET", "/api/v3/shipments?external_reference_id=Shipments%2Forders%2F99", "{\"data\":[]}");
        handler.On("GET", "/api/v3/parcels/383707309/documents/label", "%PDF-1.4 label", mediaType: "application/pdf");

        var found = await carrier.FindBookingAsync(Request(), default);
        found.Status.Should().Be(CarrierBookingLookupStatus.Found);
        found.Result!.Parcels.Single().TrackingNumber.Should().Be("3SYZXG8498635");

        var missing = await carrier.FindBookingAsync(Request() with { ShipmentId = "Shipments/orders/99" }, default);
        missing.Status.Should().Be(CarrierBookingLookupStatus.NotFound);

        // The Returns API has no filter on the external reference: a lost return answer cannot be looked up.
        (await carrier.FindBookingAsync(Request() with { IsReturn = true }, default)).Status.Should().Be(CarrierBookingLookupStatus.Unsupported);
    }

    [Fact]
    public async Task Void_cancels_the_whole_shipment_and_counts_queued_as_voided()
    {
        var (carrier, handler) = Sendcloud();
        // Documented bodies of POST /shipments/{id}/cancel.
        handler.On("POST", "/api/v3/shipments/XXX-Shipment-id/cancel", "{\"data\":{\"status\":\"queued\",\"message\":\"Shipment cancellation has been queued\"}}", HttpStatusCode.Accepted);
        handler.On("POST", "/api/v3/shipments/delivered/cancel", "{\"errors\":[{\"status\":\"409\",\"code\":\"invalid\",\"detail\":\"This shipment is already being cancelled.\"}]}", HttpStatusCode.Conflict);

        carrier.Capabilities.VoidsWholeShipment.Should().BeTrue();
        (await carrier.VoidAsync(new CarrierParcelReference("383707309", "3SYZXG8498635", null, CarrierShipmentId: "XXX-Shipment-id"), default)).Should().BeTrue();
        (await carrier.VoidAsync(new CarrierParcelReference("1", "T", null, CarrierShipmentId: "delivered"), default)).Should().BeFalse();
        (await carrier.VoidAsync(new CarrierParcelReference("67880", "T", null, CarrierShipmentId: "return:12345"), default)).Should().BeFalse();
    }

    [Fact]
    public async Task Tracking_reads_the_v3_events_by_phase()
    {
        var (carrier, handler) = Sendcloud();
        handler.On("GET", "/api/v3/parcels/tracking/3SYZXG8498635", CarrierFixtures.Text("Sendcloud", "tracking.json"));

        var events = await carrier.GetTrackingAsync(new CarrierParcelReference("383707309", "3SYZXG8498635", null), default);
        events.Select(e => e.Status).Should().Equal([TrackingStatus.InTransit, TrackingStatus.InTransit, TrackingStatus.Delivered]);
        events[0].OccurredAt.Should().Be(DateTimeOffset.Parse("2024-01-02T00:00:00Z"));
        events[1].CarrierStatus.Should().Be("in_transit/not_home");
        events.Select(e => e.EventId).Distinct().Should().HaveCount(3);
        (await carrier.GetTrackingAsync(new CarrierParcelReference("383707309", "3SYZXG8498635", null), default)).Select(e => e.EventId)
            .Should().Equal(events.Select(e => e.EventId), "a re-read is deduplicated on the same ids");
    }

    [Fact]
    public async Task Service_points_read_lockers_hours_and_skip_expired_points()
    {
        var (carrier, handler) = Sendcloud(new SendcloudOptions { PublicKey = "p", SecretKey = "s", ServicePointCarriers = ["postnl", "bpost"] });
        handler.On("GET", "/api/v3/service-points", CarrierFixtures.Text("Sendcloud", "service-points-v3.json"));
        var points = await carrier.SearchAsync(new ServicePointQuery { Country = "NL", PostalCode = "5611AG", HouseNumber = "1", Limit = 10 }, default);

        points.Should().HaveCount(2);
        points[0].Id.Should().Be("10875349");
        points[0].Kind.Should().Be(ServicePointKind.PickupPoint);
        points[0].Address.Country.Should().Be("NL");
        points[0].Latitude.Should().Be(51.441444);
        points[0].DistanceMetres.Should().Be(381);
        points[0].OpeningHours["monday"].Should().Equal(["10:00-20:00"]);
        points[0].OpeningHours["tuesday"].Should().Equal(["08:00-12:00", "13:00-18:00"]);
        points[0].OpeningHours.ContainsKey("sunday").Should().BeFalse();
        points[1].Kind.Should().Be(ServicePointKind.ParcelLocker);
        var query = handler.Requests.Single().Uri.Query;
        query.Should().Contain("country_code=NL").And.Contain("carrier_code=postnl").And.Contain("carrier_code=bpost")
            .And.Contain("address_postal_code=5611AG").And.Contain("address_house_number=1");
    }

    [Fact]
    public void Credentials_go_only_to_the_configured_API_host()
    {
        var options = new SendcloudOptions();
        SendcloudApi.IsSendcloudUrl("https://panel.sendcloud.sc/api/v3/parcels/1/documents/label", options).Should().BeTrue();
        SendcloudApi.IsSendcloudUrl("https://servicepoints.sendcloud.sc/api/v2/service-points", options).Should().BeFalse();
        SendcloudApi.IsSendcloudUrl("https://evil.example/api/v3/parcels", options).Should().BeFalse();
        SendcloudApi.IsSendcloudUrl("file:///etc/passwd", options).Should().BeFalse();
    }

    // ---- Webhooks (unchanged by v3) --------------------------------------------------------------------------------

    [Fact]
    public void Webhooks_verify_with_the_documented_test_vector_against_any_configured_key()
    {
        // sendcloud.dev webhooks: body {"key": "value"}, secret "secretkey".
        var body = Encoding.UTF8.GetBytes("{\"key\": \"value\"}");
        WebhookRequest Signed(string signature) => new([new("Sendcloud-Signature", new StringValues(signature))], body);
        const string valid = "1eed4b3d41f4653ac64fd56f1bf1cbfd349e4482cbc11dff7134bd93e5da4b0a";

        SendcloudShippingExtensions.Verification(new SendcloudOptions { SecretKey = "other", Accounts = { ["seller"] = new SendcloudAccount { SecretKey = "secretkey" } } })
            .Verify(Signed(valid)).IsAccepted.Should().BeTrue();
        SendcloudShippingExtensions.Verification(new SendcloudOptions { SecretKey = "other", WebhookSignatureKey = "secretkey" })
            .Verify(Signed(valid)).IsAccepted.Should().BeTrue();
        SendcloudShippingExtensions.Verification(new SendcloudOptions { SecretKey = "secretkey" })
            .Verify(Signed("0" + valid[1..])).IsAccepted.Should().BeFalse();
        SendcloudShippingExtensions.Verification(new SendcloudOptions()).Verify(Signed(valid)).IsAccepted.Should().BeFalse();
    }

    [Fact]
    public void Status_webhooks_become_events_with_a_stable_id_and_the_carriers_time()
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
    [InlineData("READY_TO_SEND", "Ready to send", TrackingStatus.Labelled)]
    [InlineData("ANNOUNCEMENT_FAILED", "Announcement Failed", TrackingStatus.Exception)]
    [InlineData("3", "En route to sorting center", TrackingStatus.InTransit)]
    [InlineData("12", "Awaiting customer pickup", TrackingStatus.AvailableForPickup)]
    [InlineData("80", "Unable to deliver", TrackingStatus.Exception)]
    [InlineData("999", "Parcel en route", TrackingStatus.InTransit)]
    public void Webhook_statuses_map_by_id_or_code_then_by_message(string id, string? message, TrackingStatus expected)
        => SendcloudCarrier.MapStatus(id, message).Should().Be(expected);

    // ---- bpost lookup ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Bpost_finds_an_order_by_its_reference_and_labels_it_again_or_answers_not_found()
    {
        var handler = new CarrierFakeHandler();
        var options = new BpostOptions { AccountId = "107423", PassPhrase = "passphrase_placeholder" };
        var carrier = new BpostCarrier(new FakeHttpClientFactory(handler), new StaticOptionsMonitor<BpostOptions>(options), TimeProvider.System);
        var request = Request(service: "bpack 24h Pro", parcels: 2) with { ShipFrom = new ShippingAddress { Name = "Shop", Street = "Rue", HouseNumber = "1", PostalCode = "1000", City = "Brussels", Country = "BE" } };

        (await carrier.FindBookingAsync(request, default)).Status.Should().Be(CarrierBookingLookupStatus.NotFound);

        handler.On("GET", "/services/shm/107423/orders/Shipments-orders-12345", CarrierFixtures.Text("Bpost", "order-fetched.xml"), mediaType: "application/xml");
        handler.On("POST", "/services/shm/107423/orders/Shipments-orders-12345/labels/A6", CarrierFixtures.Text("Bpost", "labels.xml"), mediaType: "application/xml");
        var found = await carrier.FindBookingAsync(request, default);
        found.Status.Should().Be(CarrierBookingLookupStatus.Found);
        found.Result!.Parcels.Should().HaveCount(2);
        carrier.Capabilities.VoidsWholeShipment.Should().BeTrue();
    }
}
