using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Shipping.Sendcloud;

/// <summary>
/// Sendcloud (D14), written against the documented API v3 (v2 parcel creation is closed to accounts created on or
/// after 13 April 2026). Outbound shipments go to <c>POST /shipments/announce</c> (synchronous: the carrier's answer and
/// labels in one call, up to 15 parcels, each with its own label and tracking, GB5); returns to
/// <c>POST /returns/announce-synchronously</c>; labels and customs declarations are downloaded per parcel from
/// <c>/parcels/{id}/documents/{type}</c> in the asked format (PDF, ZPL, PNG). Our booking key travels as
/// <c>external_reference_id</c>, which Sendcloud keeps unique per account: a repeated announce answers 409 with the
/// shipment it already holds, and <c>GET /shipments?external_reference_id=</c> finds a booking whose answer was lost
/// (<see cref="IShippingBookingLookup"/>). Tracking arrives by the signed <c>parcel_status_changed</c> webhook.
/// </summary>
/// <remarks>
/// Dangerous-goods data (<c>parcel_items[].dangerous_goods</c>) needs IATA/ADR fields Shipping does not hold, so this
/// adapter sends no marks: a service accepts a class only when the host's contract and configuration say so (GC9).
/// </remarks>
internal sealed class SendcloudCarrier(SendcloudApi api) : IShippingCarrier, IServicePointProvider, IShippingBookingLookup
{
    public const string CarrierName = "sendcloud";

    /// <summary>The synchronous announce takes at most 15 parcels per shipment.</summary>
    internal const int MaxParcels = 15;

    public string Name => CarrierName;

    string IServicePointProvider.Carrier => CarrierName;

    public CarrierCapabilities Capabilities { get; } = new()
    {
        LabelFormats = [LabelFormat.Pdf, LabelFormat.Zpl, LabelFormat.Png],
        VoidLabel = true,
        // POST /shipments/{id}/cancel cancels the whole shipment.
        VoidsWholeShipment = true,
        ReturnLabels = true,
        MultiParcel = true,
        ServicePoints = true,
        CustomsDocuments = true,
        DangerousGoodsMarks = false,
        TrackingWebhooks = true,
        TrackingPull = true,
    };

    /// <summary>Sendcloud's shipping options depend on the account's contracts: the host lists them (<see cref="SendcloudOptions.Services"/>).</summary>
    public IReadOnlyList<CarrierService> Services => CarrierCatalog.Apply([], api.Options.Services);

    public async Task<CarrierShipmentResult> CreateShipmentAsync(CarrierShipmentRequest request, CancellationToken cancellationToken)
    {
        if (request.IsReturn)
            return await CreateReturnAsync(request, cancellationToken);

        var body = BuildShipment(request, api.Options);
        var (status, response) = await api.SendAsync(HttpMethod.Post, api.Api("shipments/announce"), body, request.Account, cancellationToken);
        // 409: a shipment with this external_reference_id exists, and is answered as it is: a repeated booking books nothing.
        if (status is not (HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Conflict))
            throw SendcloudApi.Error(status, response);
        var shipment = ReadShipment(Data(response));
        return await LabelsAsync(shipment, request, cancellationToken);
    }

    /// <summary>
    /// The shipment announced with this booking key (<c>GET /shipments?external_reference_id=</c>). Returns cannot be
    /// looked up: the Returns API has no filter on the external reference.
    /// </summary>
    public async Task<CarrierBookingLookup> FindBookingAsync(CarrierShipmentRequest request, CancellationToken cancellationToken)
    {
        if (request.IsReturn)
            return CarrierBookingLookup.Unsupported;
        var response = await api.GetAsync(api.Api("shipments?external_reference_id=" + Uri.EscapeDataString(request.ShipmentId)), request.Account, cancellationToken);
        if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            return CarrierBookingLookup.NotFound;
        SendcloudShipment shipment;
        try
        {
            shipment = ReadShipment(data[0]);
        }
        catch (CarrierException ex) when (ex.StatusCode == 422)
        {
            // Its announcement failed: no label exists. Booking again meets the refusal and moves to a fresh reference.
            return CarrierBookingLookup.NotFound;
        }
        return CarrierBookingLookup.Found(await LabelsAsync(shipment, request, cancellationToken));
    }

    public async Task<bool> VoidAsync(CarrierParcelReference parcel, CancellationToken cancellationToken)
    {
        var shipmentId = parcel.CarrierShipmentId;
        if (shipmentId is not { Length: > 0 } || shipmentId.StartsWith(ReturnPrefix, StringComparison.Ordinal))
        {
            // A return, or a parcel stored without its shipment: find the shipment by the parcel id.
            if (shipmentId is { Length: > 0 } && shipmentId.StartsWith(ReturnPrefix, StringComparison.Ordinal))
                return false; // returns are cancelled in Sendcloud's panel; the API's cancellation is a request, not a void
            var found = await api.GetAsync(api.Api("shipments?ids=" + Uri.EscapeDataString(parcel.CarrierParcelId)), parcel.Account, cancellationToken);
            if (!found.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0 || Text(data[0], "id") is not { } id)
                return false;
            shipmentId = id;
        }
        // 200 cancelled, 202 queued (cancelled unless the carrier scans it within 14 days); 404, 409 cannot.
        var (status, _) = await api.SendAsync(HttpMethod.Post, api.Api($"shipments/{Uri.EscapeDataString(shipmentId)}/cancel"), null, parcel.Account, cancellationToken);
        return status is HttpStatusCode.OK or HttpStatusCode.Accepted;
    }

    /// <summary>The parcel's tracking (<c>GET /parcels/tracking/{tracking_number}</c>), one event per carrier event.</summary>
    public async Task<IReadOnlyList<CarrierTrackingEvent>> GetTrackingAsync(CarrierParcelReference parcel, CancellationToken cancellationToken)
    {
        if (parcel.TrackingNumber is not { Length: > 0 } number)
            return [];
        var (status, response) = await api.SendAsync(HttpMethod.Get, api.Api("parcels/tracking/" + Uri.EscapeDataString(number)), null, parcel.Account, cancellationToken);
        if (status == HttpStatusCode.NotFound)
            return [];
        if ((int)status is < 200 or >= 300)
            throw SendcloudApi.Error(status, response);
        return ReadTracking(response, number, parcel.CarrierParcelId);
    }

    public Task<IReadOnlyList<CarrierTrackingEvent>> ParseWebhookAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        using var document = delivery.ReadJson();
        return Task.FromResult(ParseWebhook(document.RootElement));
    }

    public async Task<IReadOnlyList<ServicePoint>> SearchAsync(ServicePointQuery query, CancellationToken cancellationToken)
    {
        var response = await api.GetAsync(api.Api("service-points?" + ServicePointQueryString(query, api.Options)), query.Account, cancellationToken);
        return ReadServicePoints(response).Take(query.Limit).ToList();
    }

    // ---- Booking ---------------------------------------------------------------------------------------------------

    private const string ReturnPrefix = "return:";

    private async Task<CarrierShipmentResult> CreateReturnAsync(CarrierShipmentRequest request, CancellationToken cancellationToken)
    {
        var body = BuildReturn(request, api.Options);
        var created = await api.PostAsync(api.Api("returns/announce-synchronously"), body, request.Account, cancellationToken);
        var returnId = Text(created, "return_id") ?? throw new CarrierException(CarrierName, "answered a return without a return_id.");
        var parcelId = Text(created, "parcel_id") ?? throw new CarrierException(CarrierName, "answered a return without a parcel_id.");
        var details = await api.GetAsync(api.Api("returns/" + Uri.EscapeDataString(returnId)), request.Account, cancellationToken);
        var label = await DocumentAsync(parcelId, "label", request.LabelFormat, request.Account, cancellationToken)
            ?? throw new CarrierException(CarrierName, $"return parcel {parcelId} has no label.");
        return new CarrierShipmentResult(ReturnPrefix + returnId,
            [new CarrierParcelResult(parcelId, Text(details, "tracking_number") ?? string.Empty, Text(details, "tracking_url"), label)]);
    }

    private async Task<CarrierShipmentResult> LabelsAsync(SendcloudShipment shipment, CarrierShipmentRequest request, CancellationToken cancellationToken)
    {
        if (shipment.Parcels.Count != request.Parcels.Count)
            throw new CarrierException(CarrierName, $"answered {shipment.Parcels.Count} parcels for {request.Parcels.Count}.");
        var results = new List<CarrierParcelResult>();
        foreach (var parcel in shipment.Parcels)
        {
            var label = await DocumentAsync(parcel.Id, "label", request.LabelFormat, request.Account, cancellationToken)
                ?? throw new CarrierException(CarrierName, $"parcel {parcel.Id} has no label.");
            results.Add(new CarrierParcelResult(parcel.Id, parcel.TrackingNumber ?? string.Empty, parcel.TrackingUrl, label));
        }
        CarrierDocument? customs = null;
        if (request.Customs is not null)
            customs = await DocumentAsync(shipment.Parcels[0].Id, "customs-declaration", LabelFormat.Pdf, request.Account, cancellationToken);
        return new CarrierShipmentResult(shipment.Id, results, customs);
    }

    private async Task<CarrierDocument?> DocumentAsync(string parcelId, string type, LabelFormat format, string? account, CancellationToken cancellationToken)
    {
        var settings = api.Options;
        var mediaType = format switch { LabelFormat.Zpl => CarrierDocument.Zpl, LabelFormat.Png => CarrierDocument.Png, _ => CarrierDocument.Pdf };
        var query = new List<string>();
        if (type == "label" && settings.PaperSize is "A4" or "A5" or "A6")
            query.Add("paper_size=" + settings.PaperSize);
        if (mediaType == CarrierDocument.Png)
            query.Add("dpi=300");
        var url = api.Api($"parcels/{Uri.EscapeDataString(parcelId)}/documents/{type}") + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty);
        if (await api.DownloadAsync(url, mediaType, account, cancellationToken) is not { } document)
            return null;
        var dpi = document.MediaType == CarrierDocument.Zpl ? settings.ZplDpi : (int?)null;
        return new CarrierDocument(document.Content, document.MediaType, dpi);
    }

    // ---- Mapping (internal for the fixture tests) ----------------------------------------------------------------

    /// <summary>The body of <c>POST /shipments/announce</c>.</summary>
    internal static JsonObject BuildShipment(CarrierShipmentRequest request, SendcloudOptions options)
    {
        if (request.Parcels.Count is 0 or > MaxParcels)
            throw new CarrierException(CarrierName, $"a shipment holds 1 to {MaxParcels} parcels; book {request.Parcels.Count} as several shipments.");
        var body = new JsonObject
        {
            ["from_address"] = options.SenderAddressId is { } sender ? new JsonObject { ["sender_address_id"] = sender } : Address(request.ShipFrom),
            ["to_address"] = Address(request.ShipTo),
            ["ship_with"] = ShipWith(request.ServiceCode, options),
            ["order_number"] = request.Reference,
            ["external_reference_id"] = request.ShipmentId,
        };
        var parcels = new JsonArray();
        foreach (var (spec, index) in request.Parcels.Select((p, i) => (p, i)))
        {
            var parcel = new JsonObject { ["weight"] = new JsonObject { ["value"] = Kilograms(spec.WeightGrams), ["unit"] = "kg" } };
            if (spec.LengthMm > 0 && spec.WidthMm > 0 && spec.HeightMm > 0)
            {
                parcel["dimensions"] = new JsonObject
                {
                    ["length"] = Centimetres(spec.LengthMm),
                    ["width"] = Centimetres(spec.WidthMm),
                    ["height"] = Centimetres(spec.HeightMm),
                    ["unit"] = "cm",
                };
            }
            // The declaration's lines travel once, with the first parcel: the declaration covers the whole shipment.
            if (index == 0 && request.Customs is { } customs)
                parcel["parcel_items"] = Items(customs);
            parcels.Add(parcel);
        }
        body["parcels"] = parcels;
        if (request.ServicePointId is { Length: > 0 } point)
            body["to_service_point"] = new JsonObject { ["id"] = point };
        if (request.Customs is { } declaration)
        {
            var currency = declaration.Items.FirstOrDefault()?.Value.Currency ?? "EUR";
            body["customs_information"] = new JsonObject
            {
                ["invoice_number"] = declaration.InvoiceNumber ?? request.Reference,
                ["export_reason"] = ExportReason(declaration.ContentType),
            };
            body["total_order_price"] = Price(declaration.TotalValue(currency).Amount, currency);
        }
        body["label_details"] = request.LabelFormat switch
        {
            LabelFormat.Zpl => new JsonObject { ["mime_type"] = CarrierDocument.Zpl, ["dpi"] = options.ZplDpi },
            LabelFormat.Png => new JsonObject { ["mime_type"] = CarrierDocument.Png, ["dpi"] = 300 },
            _ => new JsonObject { ["mime_type"] = CarrierDocument.Pdf, ["dpi"] = 72 },
        };
        return body;
    }

    /// <summary>
    /// The body of <c>POST /returns/announce-synchronously</c>: from the buyer (<see cref="CarrierShipmentRequest.ShipFrom"/>)
    /// to the seller, one parcel (Sendcloud counts further colli with <c>collo_count</c>, of the same weight).
    /// </summary>
    internal static JsonObject BuildReturn(CarrierShipmentRequest request, SendcloudOptions options)
    {
        if (request.Parcels.Count != 1)
            throw new CarrierException(CarrierName, "a return label covers one parcel: book one return per parcel.");
        var spec = request.Parcels[0];
        var shipWith = new JsonObject { ["type"] = "shipping_option_code", ["shipping_option_code"] = request.ServiceCode };
        if (options.ContractIds.TryGetValue(request.ServiceCode, out var contract))
            shipWith["contract"] = contract;
        var body = new JsonObject
        {
            ["from_address"] = Address(request.ShipFrom),
            ["to_address"] = Address(request.ShipTo),
            ["ship_with"] = shipWith,
            ["weight"] = new JsonObject { ["value"] = Kilograms(spec.WeightGrams), ["unit"] = "kg" },
            ["order_number"] = request.Reference,
            ["external_reference"] = request.ShipmentId,
            ["send_tracking_emails"] = false,
            ["apply_rules"] = false,
        };
        if (spec.LengthMm > 0 && spec.WidthMm > 0 && spec.HeightMm > 0)
        {
            body["dimensions"] = new JsonObject
            {
                ["length"] = Centimetres(spec.LengthMm),
                ["width"] = Centimetres(spec.WidthMm),
                ["height"] = Centimetres(spec.HeightMm),
                ["unit"] = "cm",
            };
        }
        if (request.Customs is { } customs)
        {
            var invoice = customs.InvoiceNumber ?? request.Reference;
            body["parcel_items"] = Items(customs);
            body["customs_invoice_nr"] = invoice;
            // For a return the export reason is always returned goods.
            body["customs_information"] = new JsonObject { ["invoice_number"] = invoice, ["export_reason"] = "returned_goods" };
        }
        return body;
    }

    private static JsonObject ShipWith(string serviceCode, SendcloudOptions options)
    {
        if (!serviceCode.Contains(':', StringComparison.Ordinal))
            throw new CarrierException(CarrierName, $"service '{serviceCode}' is not a Sendcloud shipping_option_code (like 'postnl:standard').");
        var properties = new JsonObject { ["shipping_option_code"] = serviceCode };
        if (options.ContractIds.TryGetValue(serviceCode, out var contract))
            properties["contract_id"] = contract;
        return new JsonObject { ["type"] = "shipping_option_code", ["properties"] = properties };
    }

    private static JsonObject Address(ShippingAddress address)
    {
        var json = new JsonObject
        {
            ["name"] = address.Name,
            ["address_line_1"] = address.Street,
            ["postal_code"] = address.PostalCode,
            ["city"] = address.City,
            ["country_code"] = address.Country,
        };
        if (address.HouseNumber is { Length: > 0 } number) json["house_number"] = number;
        if (address.Addition is { Length: > 0 } addition) json["address_line_2"] = addition;
        if (address.Company is { Length: > 0 } company) json["company_name"] = company;
        if (address.Email is { Length: > 0 } email) json["email"] = email;
        if (address.Phone is { Length: > 0 } phone) json["phone_number"] = phone;
        // ISO 3166-2 (IT-RM), required for some countries only.
        if (address.Subdivision is { Length: > 0 } subdivision) json["state_province_code"] = subdivision;
        return json;
    }

    /// <summary>Customs lines: per single item, as the schema states (weight a number in kg, price a decimal string).</summary>
    private static JsonArray Items(CustomsDeclaration customs) => new(customs.Items.Select(item =>
    {
        var quantity = Math.Max(1, item.Quantity);
        var line = new JsonObject
        {
            ["description"] = item.Description,
            ["quantity"] = item.Quantity,
            ["weight"] = new JsonObject { ["value"] = Math.Max(1, item.WeightGrams / quantity) / 1000m, ["unit"] = "kg" },
            ["price"] = Price(item.Value.Amount / quantity, item.Value.Currency),
            ["hs_code"] = item.HsCode,
            ["origin_country"] = item.OriginCountry,
        };
        if (item.Sku is { Length: > 0 } sku) line["sku"] = sku;
        return (JsonNode)line;
    }).ToArray());

    private static JsonObject Price(decimal amount, string currency) => new() { ["value"] = Amount(amount), ["currency"] = currency };

    /// <summary>v3's <c>export_reason</c>: gift, documents, commercial_goods, commercial_sample, returned_goods.</summary>
    internal static string ExportReason(CustomsContentType type) => type switch
    {
        CustomsContentType.Gift => "gift",
        CustomsContentType.Documents => "documents",
        CustomsContentType.CommercialSample => "commercial_sample",
        CustomsContentType.ReturnedGoods => "returned_goods",
        _ => "commercial_goods",
    };

    internal sealed record SendcloudParcelInfo(string Id, string? TrackingNumber, string? TrackingUrl, string? StatusCode);

    internal sealed record SendcloudShipment(string Id, IReadOnlyList<SendcloudParcelInfo> Parcels);

    /// <summary>The <c>data</c> of an answer, or the answer itself when it has none.</summary>
    internal static JsonElement Data(JsonElement response)
        => response.ValueKind == JsonValueKind.Object && response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : response;

    /// <summary>
    /// Reads a v3 shipment. A failed announcement still answers 201: a parcel in <c>ANNOUNCEMENT_FAILED</c> or an
    /// <c>errors</c> entry throws with the carrier's message, as a refusal (422), so the next attempt books under a
    /// fresh reference.
    /// </summary>
    internal static SendcloudShipment ReadShipment(JsonElement shipment)
    {
        var parcels = shipment.TryGetProperty("parcels", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(p => new SendcloudParcelInfo(
                Text(p, "id") ?? throw new CarrierException(CarrierName, "answered a parcel without an id."),
                Text(p, "tracking_number"),
                Text(p, "tracking_url"),
                p.TryGetProperty("status", out var status) ? Text(status, "code") : null)).ToList()
            : [];
        var errors = shipment.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Select(x => Text(x, "detail") ?? Text(x, "code") ?? "?").ToList()
            : [];
        if (errors.Count > 0 || parcels.Any(p => p.StatusCode == "ANNOUNCEMENT_FAILED"))
            throw new CarrierException(CarrierName, "the carrier refused the announcement: " + (errors.Count > 0 ? string.Join("; ", errors) : "ANNOUNCEMENT_FAILED"), 422);
        var id = Text(shipment, "id") ?? throw new CarrierException(CarrierName, "answered a shipment without an id.");
        return new SendcloudShipment(id, parcels);
    }

    /// <summary>The events of <c>GET /parcels/tracking/{number}</c>: <c>events[]</c> with <c>event_at</c>, <c>phase</c>, <c>sub_status</c>.</summary>
    internal static IReadOnlyList<CarrierTrackingEvent> ReadTracking(JsonElement response, string trackingNumber, string? parcelId)
    {
        var root = Data(response);
        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<CarrierTrackingEvent>();
        foreach (var item in events.EnumerateArray())
        {
            if (Text(item, "event_at") is not { } at || !DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var occurred))
                continue;
            var phase = Text(item, "phase") ?? string.Empty;
            var subStatus = Text(item, "sub_status");
            var description = Text(item, "description");
            var code = subStatus is { Length: > 0 } and not "none" ? $"{phase}/{subStatus}" : phase;
            var eventId = "sc_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{trackingNumber}|{code}|{at}")))[..32];
            result.Add(new CarrierTrackingEvent(eventId, trackingNumber, parcelId, MapPhase(phase, Text(item, "exception"), description), code, description, occurred));
        }
        return result;
    }

    /// <summary>
    /// v3 tracking phases. Documented: <c>accepted</c>, <c>in_transit</c>, <c>delivered</c> (examples) and an
    /// <c>exception</c> field (<c>none</c> when there is none). The full phase list is on Sendcloud's status page,
    /// not in the reference: other phases are classified by their name and the description.
    /// </summary>
    internal static TrackingStatus MapPhase(string phase, string? exception, string? description)
    {
        var name = phase.ToLowerInvariant();
        if (name.Contains("deliver") && !name.Contains("out_for") && !name.Contains("attempt") && !name.Contains("fail"))
            return TrackingStatus.Delivered;
        if (exception is { Length: > 0 } and not "none")
            return TrackingStatus.Exception;
        return name switch
        {
            "announced" or "created" or "ready_to_send" or "label_created" => TrackingStatus.Labelled,
            "accepted" or "in_transit" or "sorted" => TrackingStatus.InTransit,
            "out_for_delivery" => TrackingStatus.OutForDelivery,
            "cancelled" or "canceled" => TrackingStatus.Cancelled,
            _ when name.Contains("return") => TrackingStatus.ReturnedToSender,
            _ when name.Contains("pickup") || name.Contains("pick_up") || name.Contains("collect") => TrackingStatus.AvailableForPickup,
            _ => ByMessage(description),
        };
    }

    // ---- Webhook (the payload is the parcel as retrieved, unchanged by v3) -----------------------------------------

    internal static IReadOnlyList<CarrierTrackingEvent> ParseWebhook(JsonElement root)
    {
        if (Text(root, "action") != "parcel_status_changed" || !root.TryGetProperty("parcel", out var parcel))
            return [];
        var at = UnixTime(root, "carrier_status_change_timestamp") ?? UnixTime(root, "timestamp");
        var parsed = ParcelEvent(parcel, at, Text(root, "timestamp"));
        return parsed is null ? [] : [parsed];
    }

    private static CarrierTrackingEvent? ParcelEvent(JsonElement parcel, DateTimeOffset? at, string? stamp)
    {
        if (Text(parcel, "tracking_number") is not { Length: > 0 } tracking || !parcel.TryGetProperty("status", out var status))
            return null;
        var id = Text(status, "id") ?? Text(status, "code") ?? "?";
        var message = Text(status, "message");
        var occurred = at ?? DateTimeOffset.UnixEpoch;
        // Sendcloud sends no event id: the digest of parcel, status and time is stable across redeliveries.
        var eventId = "sc_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Text(parcel, "id")}|{id}|{stamp ?? occurred.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}")))[..32];
        return new CarrierTrackingEvent(eventId, tracking, Text(parcel, "id"), MapStatus(id, message), id, message, occurred);
    }

    /// <summary>
    /// The webhook's status: a numeric id (v2 numbering, still sent) or a v3 code. Confirmed in the docs: 1000 ready to
    /// send, 1002 announcement failed, 2000 cancelled, 11 delivered, 3 en route to sorting centre, 4 delivery delayed,
    /// 5 sorted, 6 not sorted; v3 codes READY_TO_SEND, ANNOUNCING, ANNOUNCEMENT_FAILED. Others are classified by their message.
    /// </summary>
    internal static TrackingStatus MapStatus(string id, string? message) => id switch
    {
        "11" or "DELIVERED" => TrackingStatus.Delivered,
        "2000" or "CANCELLED" => TrackingStatus.Cancelled,
        "1000" or "READY_TO_SEND" or "ANNOUNCING" => TrackingStatus.Labelled,
        "1002" or "ANNOUNCEMENT_FAILED" => TrackingStatus.Exception,
        "3" or "5" or "6" => TrackingStatus.InTransit,
        "4" => TrackingStatus.Exception,
        _ => ByMessage(message),
    };

    private static TrackingStatus ByMessage(string? message)
    {
        var text = (message ?? string.Empty).ToLowerInvariant();
        if (text.Contains("ready to send") || text.Contains("announced")) return TrackingStatus.Labelled;
        if (text.Contains("returned")) return TrackingStatus.ReturnedToSender;
        if (text.Contains("cancel")) return TrackingStatus.Cancelled;
        if (text.Contains("pickup") || text.Contains("pick up") || text.Contains("pick-up")) return TrackingStatus.AvailableForPickup;
        if (text.Contains("out for delivery")) return TrackingStatus.OutForDelivery;
        if (text.Contains("delivered") && !text.Contains("not delivered") && !text.Contains("unable")) return TrackingStatus.Delivered;
        if (text.Contains("unable") || text.Contains("delay") || text.Contains("exception") || text.Contains("not delivered")) return TrackingStatus.Exception;
        return TrackingStatus.InTransit;
    }

    // ---- Service points (v3: GET /service-points on the panel host) -----------------------------------------------

    internal static string ServicePointQueryString(ServicePointQuery query, SendcloudOptions options)
    {
        var parameters = new List<string> { "country_code=" + Uri.EscapeDataString(query.Country) };
        if (options.ServicePointCarriers is { Count: > 0 } carriers)
            parameters.AddRange(carriers.Select(c => "carrier_code=" + Uri.EscapeDataString(c)));
        else
            parameters.Add("use_integration_carriers=true");
        // The structured reference point: house number only together with street or postal code.
        parameters.Add("address_postal_code=" + Uri.EscapeDataString(query.PostalCode));
        if (query.Street is { Length: > 0 } street) parameters.Add("address_street=" + Uri.EscapeDataString(street));
        if (query.HouseNumber is { Length: > 0 } number) parameters.Add("address_house_number=" + Uri.EscapeDataString(number));
        if (query.City is { Length: > 0 } city) parameters.Add("address_city=" + Uri.EscapeDataString(city));
        if (query.Kind is { } kind)
            parameters.Add("general_shop_type=" + (kind == ServicePointKind.ParcelLocker ? "locker" : "servicepoint"));
        parameters.Add("limit=" + Math.Clamp(query.Limit, 1, 200).ToString(CultureInfo.InvariantCulture));
        return string.Join('&', parameters);
    }

    /// <summary><c>{"data":{"results":[…]}}</c>; expired points are skipped; <c>opening_times</c> are per weekday name.</summary>
    internal static IEnumerable<ServicePoint> ReadServicePoints(JsonElement response)
    {
        var data = Data(response);
        if (!data.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var point in results.EnumerateArray())
        {
            if (point.TryGetProperty("is_expired", out var expired) && expired.ValueKind == JsonValueKind.True)
                continue;
            var hours = new Dictionary<string, IReadOnlyList<string>>();
            if (point.TryGetProperty("opening_times", out var times) && times.ValueKind == JsonValueKind.Object)
            {
                foreach (var day in times.EnumerateObject())
                {
                    if (day.Value.ValueKind != JsonValueKind.Array)
                        continue;
                    hours[day.Name.ToLowerInvariant()] = day.Value.EnumerateArray()
                        .Select(slot => $"{Text(slot, "start_time")}-{Text(slot, "end_time")}")
                        .ToList();
                }
            }
            var address = point.TryGetProperty("address", out var a) ? a : default;
            var position = point.TryGetProperty("position", out var p) ? p : default;
            var name = Text(point, "name") ?? string.Empty;
            yield return new ServicePoint
            {
                Id = Text(point, "id") ?? string.Empty,
                Carrier = CarrierName,
                Name = name,
                Kind = string.Equals(Text(point, "general_shop_type"), "locker", StringComparison.OrdinalIgnoreCase) ? ServicePointKind.ParcelLocker : ServicePointKind.PickupPoint,
                Address = new ShippingAddress
                {
                    Name = name,
                    Street = Text(address, "street") ?? string.Empty,
                    HouseNumber = Text(address, "house_number"),
                    PostalCode = Text(address, "postal_code") ?? string.Empty,
                    City = Text(address, "city") ?? string.Empty,
                    Country = Text(address, "country_code") ?? string.Empty,
                },
                Latitude = Number(position, "latitude"),
                Longitude = Number(position, "longitude"),
                DistanceMetres = Number(point, "distance") is { } distance ? (int)Math.Round(distance) : null,
                OpeningHours = hours,
            };
        }
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    private static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static double? Number(JsonElement element, string name)
        => Text(element, name) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>A unix timestamp; the docs do not state the unit, so values past 10^11 are read as milliseconds.</summary>
    private static DateTimeOffset? UnixTime(JsonElement element, string name)
    {
        if (Number(element, name) is not { } value || value <= 0)
            return null;
        return value > 100_000_000_000d ? DateTimeOffset.FromUnixTimeMilliseconds((long)value) : DateTimeOffset.FromUnixTimeSeconds((long)value);
    }

    private static string Kilograms(int grams) => (grams / 1000m).ToString("0.000", CultureInfo.InvariantCulture);

    private static string Centimetres(int millimetres) => (millimetres / 10m).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Amount(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);
}
