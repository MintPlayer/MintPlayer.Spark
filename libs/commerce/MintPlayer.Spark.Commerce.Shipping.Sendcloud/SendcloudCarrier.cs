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
/// Sendcloud (D14), written against the documented v2 parcels API. Every parcel of a shipment is created in one batch
/// call (<c>{"parcels":[…]}</c>) with <c>request_label</c>, so each has its own label and tracking (GB5); returns use
/// <c>is_return</c>, service points <c>to_service_point</c>, customs <c>customs_information</c> + <c>parcel_items</c>
/// with HS code and origin (GB6). Tracking arrives by the signed <c>parcel_status_changed</c> webhook.
/// </summary>
/// <remarks>
/// <b>Version.</b> Sendcloud closed v2 parcel creation to accounts created on or after 13 April 2026 (its v3
/// <c>/shipments/announce</c> replaces it). This adapter targets v2; an account created after that date needs the v3
/// mapping, which the fixtures do not cover yet (spike S13). Dangerous-goods data (<c>parcel_items[].dangerous_goods</c>)
/// needs IATA/ADR fields Shipping does not hold, so this adapter sends no marks: a service accepts a class only when the
/// host's contract and configuration say so (GC9).
/// </remarks>
internal sealed class SendcloudCarrier(SendcloudApi api) : IShippingCarrier, IServicePointProvider
{
    public const string CarrierName = "sendcloud";

    public string Name => CarrierName;

    string IServicePointProvider.Carrier => CarrierName;

    public CarrierCapabilities Capabilities { get; } = new()
    {
        LabelFormats = [LabelFormat.Pdf],
        VoidLabel = true,
        ReturnLabels = true,
        MultiParcel = true,
        ServicePoints = true,
        CustomsDocuments = true,
        DangerousGoodsMarks = false,
        TrackingWebhooks = true,
        TrackingPull = true,
    };

    /// <summary>Sendcloud's methods depend on the account's contracts: the host lists them (<see cref="SendcloudOptions.Services"/>).</summary>
    public IReadOnlyList<CarrierService> Services => CarrierCatalog.Apply([], api.Options.Services);

    public async Task<CarrierShipmentResult> CreateShipmentAsync(CarrierShipmentRequest request, CancellationToken cancellationToken)
    {
        var body = BuildRequest(request, api.Options);
        var response = await api.PostAsync(api.Api("parcels?errors=verbose-carrier"), body, request.Account, cancellationToken);
        var parcels = ReadParcels(response);
        if (parcels.Count != request.Parcels.Count)
            throw new CarrierException(CarrierName, $"answered {parcels.Count} parcels for {request.Parcels.Count}.");

        var results = new List<CarrierParcelResult>();
        CarrierDocument? customs = null;
        foreach (var parcel in parcels)
        {
            var labelUrl = parcel.LabelUrl(api.Options.LabelPrinter)
                ?? throw new CarrierException(CarrierName, $"parcel {parcel.Id} has no label.");
            var (bytes, mediaType) = await api.DownloadAsync(labelUrl, request.Account, cancellationToken);
            results.Add(new CarrierParcelResult(parcel.Id, parcel.TrackingNumber ?? string.Empty, parcel.TrackingUrl, new CarrierDocument(bytes, mediaType)));
            if (customs is null && request.Customs is not null && parcel.CustomsDocumentUrl is { } customsUrl)
            {
                var (customsBytes, customsType) = await api.DownloadAsync(customsUrl, request.Account, cancellationToken);
                customs = new CarrierDocument(customsBytes, customsType);
            }
        }
        return new CarrierShipmentResult(results[0].CarrierParcelId, results, customs);
    }

    public async Task<bool> VoidAsync(CarrierParcelReference parcel, CancellationToken cancellationToken)
    {
        // 200 cancelled, 202 queued (cancelled unless the carrier scans it); 400 already shipped/old, 404, 410 cannot.
        var status = await api.PostStatusAsync(api.Api($"parcels/{Uri.EscapeDataString(parcel.CarrierParcelId)}/cancel"), parcel.Account, cancellationToken);
        return status is HttpStatusCode.OK or HttpStatusCode.Accepted;
    }

    public async Task<IReadOnlyList<CarrierTrackingEvent>> GetTrackingAsync(CarrierParcelReference parcel, CancellationToken cancellationToken)
    {
        var response = await api.GetAsync(api.Api($"parcels/{Uri.EscapeDataString(parcel.CarrierParcelId)}"), parcel.Account, cancellationToken);
        if (!response.TryGetProperty("parcel", out var element))
            return [];
        var parsed = ParcelEvent(element, null, null);
        return parsed is null ? [] : [parsed];
    }

    public Task<IReadOnlyList<CarrierTrackingEvent>> ParseWebhookAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        using var document = delivery.ReadJson();
        return Task.FromResult(ParseWebhook(document.RootElement));
    }

    public async Task<IReadOnlyList<ServicePoint>> SearchAsync(ServicePointQuery query, CancellationToken cancellationToken)
    {
        var parameters = new List<string> { "country=" + Uri.EscapeDataString(query.Country), "postal_code=" + Uri.EscapeDataString(query.PostalCode) };
        if (query.City is { Length: > 0 } city) parameters.Add("city=" + Uri.EscapeDataString(city));
        if (query.HouseNumber is { Length: > 0 } number) parameters.Add("house_number=" + Uri.EscapeDataString(number));
        if (query.Street is { Length: > 0 } street) parameters.Add("address=" + Uri.EscapeDataString(street));
        if (api.Options.ServicePointCarriers is { Count: > 0 } carriers) parameters.Add("carrier=" + Uri.EscapeDataString(string.Join(',', carriers)));
        var response = await api.GetAsync(api.ServicePoints("service-points?" + string.Join('&', parameters)), query.Account, cancellationToken);
        return ReadServicePoints(response).Take(query.Limit).ToList();
    }

    // ---- Mapping (internal for the fixture tests) ----------------------------------------------------------------

    internal static JsonObject BuildRequest(CarrierShipmentRequest request, SendcloudOptions options)
    {
        if (!int.TryParse(request.ServiceCode, NumberStyles.None, CultureInfo.InvariantCulture, out var methodId))
            throw new CarrierException(CarrierName, $"service '{request.ServiceCode}' is not a Sendcloud shipping-method id.");
        var to = request.IsReturn ? request.ShipFrom : request.ShipTo;
        var parcels = new JsonArray();
        foreach (var (spec, index) in request.Parcels.Select((p, i) => (p, i)))
        {
            var parcel = new JsonObject
            {
                ["name"] = to.Name,
                ["address"] = to.Street,
                ["house_number"] = to.HouseNumber ?? string.Empty,
                ["city"] = to.City,
                ["postal_code"] = to.PostalCode,
                ["country"] = to.Country,
                ["request_label"] = true,
                ["shipment"] = new JsonObject { ["id"] = methodId },
                ["weight"] = Kilograms(spec.WeightGrams),
                ["order_number"] = request.Reference,
                ["external_reference"] = request.Parcels.Count == 1 ? request.ShipmentId : $"{request.ShipmentId}/{index + 1}",
                ["quantity"] = 1,
            };
            if (to.Company is { Length: > 0 } company) parcel["company_name"] = company;
            if (to.Addition is { Length: > 0 } addition) parcel["address_2"] = addition;
            if (to.Email is { Length: > 0 } email) parcel["email"] = email;
            if (to.Phone is { Length: > 0 } phone) parcel["telephone"] = phone;
            if (to.Subdivision is { Length: > 0 } subdivision) parcel["country_state"] = subdivision;
            if (spec.LengthMm > 0) parcel["length"] = Centimetres(spec.LengthMm);
            if (spec.WidthMm > 0) parcel["width"] = Centimetres(spec.WidthMm);
            if (spec.HeightMm > 0) parcel["height"] = Centimetres(spec.HeightMm);
            if (request.IsReturn) parcel["is_return"] = true;
            if (options.SenderAddressId is { } sender && !request.IsReturn) parcel["sender_address"] = sender;
            if (request.ServicePointId is { } point)
            {
                if (!int.TryParse(point, NumberStyles.None, CultureInfo.InvariantCulture, out var pointId))
                    throw new CarrierException(CarrierName, $"service point '{point}' is not a Sendcloud service-point id.");
                parcel["to_service_point"] = pointId;
            }
            if (request.Customs is { } customs)
            {
                var currency = customs.Items.FirstOrDefault()?.Value.Currency ?? "EUR";
                parcel["customs_information"] = new JsonObject
                {
                    ["customs_invoice_nr"] = customs.InvoiceNumber ?? request.Reference,
                    ["customs_shipment_type"] = ShipmentType(customs.ContentType),
                };
                parcel["total_order_value_currency"] = currency;
                parcel["total_order_value"] = Amount(customs.TotalValue(currency).Amount);
                parcel["parcel_items"] = new JsonArray(customs.Items.Select(item => (JsonNode)new JsonObject
                {
                    ["description"] = item.Description,
                    ["quantity"] = item.Quantity,
                    // Per single item, as the schema states.
                    ["weight"] = Kilograms(Math.Max(1, item.WeightGrams / Math.Max(1, item.Quantity))),
                    ["value"] = Amount(item.Value.Amount / Math.Max(1, item.Quantity)),
                    ["hs_code"] = item.HsCode,
                    ["origin_country"] = item.OriginCountry,
                    ["sku"] = item.Sku,
                }).ToArray());
            }
            parcels.Add(parcel);
        }
        return new JsonObject { ["parcels"] = parcels };
    }

    /// <summary>Sendcloud's <c>customs_shipment_type</c>: 0 gift, 1 documents, 2 commercial goods, 3 commercial sample, 4 returned goods.</summary>
    internal static int ShipmentType(CustomsContentType type) => type switch
    {
        CustomsContentType.Gift => 0,
        CustomsContentType.Documents => 1,
        CustomsContentType.CommercialSample => 3,
        CustomsContentType.ReturnedGoods => 4,
        _ => 2,
    };

    internal sealed record SendcloudParcel(string Id, string? TrackingNumber, string? TrackingUrl, JsonElement Element)
    {
        public string? LabelUrl(string printer)
        {
            if (!Element.TryGetProperty("label", out var label))
                return null;
            if (printer == "normal_printer" && label.TryGetProperty("normal_printer", out var normal) && normal.ValueKind == JsonValueKind.Array && normal.GetArrayLength() > 0)
                return normal[0].GetString();
            return label.TryGetProperty("label_printer", out var single) && single.ValueKind == JsonValueKind.String ? single.GetString() : null;
        }

        /// <summary>The CN23 or commercial invoice in <c>documents</c>, when Sendcloud made one.</summary>
        public string? CustomsDocumentUrl => Element.TryGetProperty("documents", out var documents) && documents.ValueKind == JsonValueKind.Array
            ? documents.EnumerateArray()
                .Where(d => d.TryGetProperty("type", out var t) && t.GetString() is "cn23" or "cn23-default" or "commercial-invoice")
                .Select(d => d.TryGetProperty("link", out var link) ? link.GetString() : null)
                .FirstOrDefault(l => l is not null)
            : null;
    }

    /// <summary>Reads <c>{"parcel":…}</c> or <c>{"parcels":[…]}</c>; <c>failed_parcels</c> throws with the carrier's errors.</summary>
    internal static IReadOnlyList<SendcloudParcel> ReadParcels(JsonElement response)
    {
        if (response.TryGetProperty("failed_parcels", out var failed) && failed.ValueKind == JsonValueKind.Array && failed.GetArrayLength() > 0)
        {
            var errors = failed.EnumerateArray().Select(f => f.TryGetProperty("errors", out var e) ? e.GetRawText() : "?");
            throw new CarrierException(CarrierName, "refused the parcels: " + string.Join("; ", errors));
        }
        IEnumerable<JsonElement> elements = response.TryGetProperty("parcels", out var many) && many.ValueKind == JsonValueKind.Array
            ? many.EnumerateArray()
            : response.TryGetProperty("parcel", out var one) ? [one] : [];
        return elements.Select(e => new SendcloudParcel(
            Text(e, "id") ?? throw new CarrierException(CarrierName, "answered a parcel without an id."),
            Text(e, "tracking_number"),
            Text(e, "tracking_url"),
            e.Clone())).ToList();
    }

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
        var id = Text(status, "id") ?? "?";
        var message = Text(status, "message");
        var occurred = at ?? DateTimeOffset.UnixEpoch;
        // Sendcloud sends no event id: the digest of parcel, status and time is stable across redeliveries.
        var eventId = "sc_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Text(parcel, "id")}|{id}|{stamp ?? occurred.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}")))[..32];
        return new CarrierTrackingEvent(eventId, tracking, Text(parcel, "id"), MapStatus(id, message), id, message, occurred);
    }

    /// <summary>
    /// Sendcloud's numeric status ids. Confirmed in the docs: 1000 ready to send, 1002 announcement failed, 2000
    /// cancelled, 11 delivered, 3 en route to sorting centre, 4 delivery delayed, 5 sorted, 6 not sorted. Others (the
    /// full table is only served live by <c>GET /parcels/statuses</c>) are classified by their message.
    /// </summary>
    internal static TrackingStatus MapStatus(string id, string? message) => id switch
    {
        "11" => TrackingStatus.Delivered,
        "2000" => TrackingStatus.Cancelled,
        "1000" => TrackingStatus.Labelled,
        "1002" => TrackingStatus.Exception,
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

    internal static IEnumerable<ServicePoint> ReadServicePoints(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Array)
            yield break;
        string[] days = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];
        foreach (var point in response.EnumerateArray())
        {
            if (point.TryGetProperty("is_active", out var active) && active.ValueKind == JsonValueKind.False)
                continue;
            var hours = new Dictionary<string, IReadOnlyList<string>>();
            // Keys "0".."6"; 0 is read as Monday (inferred from the documented example, not stated).
            if (point.TryGetProperty("formatted_opening_times", out var times) && times.ValueKind == JsonValueKind.Object)
            {
                foreach (var day in times.EnumerateObject())
                {
                    if (int.TryParse(day.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index is >= 0 and < 7 && day.Value.ValueKind == JsonValueKind.Array)
                        hours[days[index]] = day.Value.EnumerateArray().Select(v => (v.GetString() ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal)).ToList();
                }
            }
            var locker = string.Equals(Text(point, "general_shop_type"), "locker", StringComparison.OrdinalIgnoreCase);
            yield return new ServicePoint
            {
                Id = Text(point, "id") ?? string.Empty,
                Carrier = CarrierName,
                Name = Text(point, "name") ?? string.Empty,
                Kind = locker ? ServicePointKind.ParcelLocker : ServicePointKind.PickupPoint,
                Address = new ShippingAddress
                {
                    Name = Text(point, "name") ?? string.Empty,
                    Street = Text(point, "street") ?? string.Empty,
                    HouseNumber = Text(point, "house_number"),
                    PostalCode = Text(point, "postal_code") ?? string.Empty,
                    City = Text(point, "city") ?? string.Empty,
                    Country = Text(point, "country") ?? string.Empty,
                },
                Latitude = Number(point, "latitude"),
                Longitude = Number(point, "longitude"),
                DistanceMetres = Number(point, "distance") is { } distance ? (int)Math.Round(distance) : null,
                OpeningHours = hours,
            };
        }
    }

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
