using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Shipping.PostNL;

/// <summary>
/// <c>Spark:Commerce:Shipping:PostNL</c>. Credentials come from configuration or user-secrets only, are read per call
/// and never logged.
/// </summary>
public sealed class PostNLOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Shipping:PostNL";

    /// <summary>The API key (header <c>apikey</c>).</summary>
    public string? ApiKey { get; set; }

    /// <summary>The customer code (four letters, <c>DEVC</c> in the sandbox).</summary>
    public string? CustomerCode { get; set; }

    /// <summary>The customer number.</summary>
    public string? CustomerNumber { get; set; }

    /// <summary>The collection location (six digits).</summary>
    public string? CollectionLocation { get; set; }

    /// <summary>Further accounts by name (warehouse WD8). A shipment's <c>Account</c> picks one.</summary>
    public Dictionary<string, PostNLAccount> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Overrides of the shipped products, and further product codes (dangerous goods: GC9).</summary>
    public Dictionary<string, CarrierServiceOptions> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The printer type for PDF labels.</summary>
    public string PdfPrinterType { get; set; } = "GraphicFile|PDF";

    /// <summary>The printer type for ZPL labels. The value is not confirmed from the published file (its printer-type page was not read); confirm it.</summary>
    public string ZplPrinterType { get; set; } = "Zebra|Generic ZPL II 200 dpi";

    /// <summary>The barcode series asked for multi-collo main barcodes (<c>Serie</c> of the Barcode API).</summary>
    public string BarcodeSerie { get; set; } = "000000000-999999999";

    /// <summary>The API base: <c>https://api.postnl.nl/</c>, or <c>https://api-sandbox.postnl.nl/</c>.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.postnl.nl/";
}

/// <summary>One PostNL account.</summary>
public sealed class PostNLAccount
{
    public string? ApiKey { get; set; }

    public string? CustomerCode { get; set; }

    public string? CustomerNumber { get; set; }

    public string? CollectionLocation { get; set; }
}

/// <summary>Registration of the PostNL adapter.</summary>
public static class PostNLShippingExtensions
{
    /// <summary>
    /// Adds PostNL (<c>postnl</c>): labels, locations and parcel lockers, and tracking by polling the Shippingstatus API
    /// (no public webhook specification was found, so no webhook source is registered). Credentials:
    /// <c>Spark:Commerce:Shipping:PostNL</c> (user-secrets).
    /// </summary>
    public static SparkShippingBuilder UsePostNL(this SparkShippingBuilder shipping, Action<PostNLOptions>? configure = null)
    {
        var builder = shipping.Spark;
        builder.Services.AddSparkConfigurationSection<PostNLOptions>();
        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddHttpClient(PostNLCarrier.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        builder.AddServicePointProvider<PostNLCarrier>();
        return builder.AddShippingCarrier<PostNLCarrier>(PostNLCarrier.CarrierName);
    }
}

/// <summary>
/// PostNL (D14), written against PostNL's published OpenAPI files: Labelling v2_2 (<c>/shipment/v2_2/label?confirm=true</c>),
/// Barcode v1_1, Locations v2_1 and Shippingstatus v2. Multi-collo shipments are one request with a <c>Groups</c>
/// entry (type 03) per parcel, which needs the main barcode first (GB5). Customs <c>Content</c> goes only on non-EU
/// shipments, as the schema demands (GB6); the CN23 and commercial invoice come back as extra labels.
/// </summary>
/// <remarks>
/// Dangerous goods (GC9): PostNL takes ADR only under a special contract, and its <c>HazardousMaterial</c> entry needs
/// substance codes Shipping does not hold, so no product accepts a class until the host's configuration says so; a
/// parcel with dangerous goods gets the reference prefix <c>ADR/LQ</c> the products page asks for.
/// </remarks>
internal sealed class PostNLCarrier(IHttpClientFactory httpClients, IOptionsMonitor<PostNLOptions> options, TimeProvider time) : IShippingCarrier, IServicePointProvider
{
    public const string CarrierName = "postnl";
    public const string HttpClientName = "spark-commerce-shipping-postnl";

    /// <summary>PostNL's status times are Dutch wall-clock times without an offset.</summary>
    private const string StatusZone = "Europe/Amsterdam";

    public string Name => CarrierName;

    string IServicePointProvider.Carrier => CarrierName;

    public CarrierCapabilities Capabilities { get; } = new()
    {
        LabelFormats = [LabelFormat.Pdf, LabelFormat.Zpl],
        VoidLabel = false,
        ReturnLabels = true,
        MultiParcel = true,
        ServicePoints = true,
        CustomsDocuments = true,
        DangerousGoodsMarks = false,
        TrackingWebhooks = false,
        TrackingPull = true,
    };

    /// <summary>
    /// The shipped products (product codes page): 3085 standard NL, 4946 NL to BE, 3285 returns, 4945 outside the EU
    /// (the swagger example), and 3533 delivery to a PostNL location (from integrators' documentation, not PostNL's
    /// own page: confirm). Hosts add or change products in <see cref="PostNLOptions.Services"/>.
    /// </summary>
    internal static readonly IReadOnlyList<CarrierService> Shipped =
    [
        new("3085", "PostNL standard") { Countries = ["NL"], FromCountries = ["NL"], MaxWeightGrams = 23_000 },
        new("4946", "PostNL Belgium") { Countries = ["BE"], FromCountries = ["NL"], MaxWeightGrams = 23_000 },
        new("3533", "PostNL pick-up point") { Countries = ["NL"], FromCountries = ["NL"], MaxWeightGrams = 23_000, ServicePoint = true },
        new("3285", "PostNL return") { Countries = ["NL"], FromCountries = ["NL"], MaxWeightGrams = 23_000, Return = true },
        new("4945", "PostNL outside the EU") { FromCountries = ["NL"], MaxWeightGrams = 20_000, CustomsStyle = CustomsDocumentStyle.PostalParcel },
    ];

    public IReadOnlyList<CarrierService> Services => CarrierCatalog.Apply(Shipped, options.CurrentValue.Services);

    public async Task<CarrierShipmentResult> CreateShipmentAsync(CarrierShipmentRequest request, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var account = Account(settings, request.Account);
        IReadOnlyList<string>? barcodes = null;
        if (request.Parcels.Count > 1)
        {
            var fetched = new List<string>();
            for (var i = 0; i < request.Parcels.Count; i++)
            {
                var query = $"shipment/v1_1/barcode?CustomerCode={Uri.EscapeDataString(account.CustomerCode)}&CustomerNumber={Uri.EscapeDataString(account.CustomerNumber)}&Type=3S&Serie={Uri.EscapeDataString(settings.BarcodeSerie)}";
                var answer = await SendAsync(HttpMethod.Get, query, null, account, cancellationToken);
                fetched.Add(answer.GetProperty("Barcode").GetString() ?? throw new CarrierException(CarrierName, "the Barcode API answered no barcode."));
            }
            barcodes = fetched;
        }
        var body = BuildRequest(request, settings, account, barcodes, time.GetUtcNow());
        var response = await SendAsync(HttpMethod.Post, "shipment/v2_2/label?confirm=true", body, account, cancellationToken);
        return ReadResponse(response, request.Parcels.Count);
    }

    /// <summary>The Labelling API has no void; an unscanned PostNL label simply expires. Shipping offers no void for PostNL.</summary>
    public Task<bool> VoidAsync(CarrierParcelReference parcel, CancellationToken cancellationToken) => Task.FromResult(false);

    public async Task<IReadOnlyList<CarrierTrackingEvent>> GetTrackingAsync(CarrierParcelReference parcel, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var response = await SendAsync(HttpMethod.Get, $"shipment/v2/status/barcode/{Uri.EscapeDataString(parcel.TrackingNumber)}?detail=false&language=EN",
            null, Account(settings, parcel.Account), cancellationToken);
        return ReadStatus(response);
    }

    public Task<IReadOnlyList<CarrierTrackingEvent>> ParseWebhookAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CarrierTrackingEvent>>([]);

    public async Task<IReadOnlyList<ServicePoint>> SearchAsync(ServicePointQuery query, CancellationToken cancellationToken)
    {
        // The Locations API serves NL and BE only.
        if (query.Country is not ("NL" or "BE"))
            return [];
        var parameters = new List<string> { "CountryCode=" + query.Country, "PostalCode=" + Uri.EscapeDataString(query.PostalCode.Replace(" ", string.Empty, StringComparison.Ordinal)) };
        if (query.City is { Length: > 0 } city) parameters.Add("City=" + Uri.EscapeDataString(city));
        if (query.Street is { Length: > 0 } street) parameters.Add("Street=" + Uri.EscapeDataString(street));
        if (query.HouseNumber is { Length: > 0 } number && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out _)) parameters.Add("HouseNumber=" + number);
        parameters.Add("DeliveryOptions=" + query.Kind switch { ServicePointKind.ParcelLocker => "PA", ServicePointKind.PickupPoint => "PG_EX", _ => "PG" });
        var response = await SendAsync(HttpMethod.Get, "shipment/v2_1/locations/nearest?" + string.Join('&', parameters), null, Account(options.CurrentValue, query.Account), cancellationToken);
        return ReadLocations(response).Take(query.Limit).ToList();
    }

    // ---- HTTP ----------------------------------------------------------------------------------------------------

    internal sealed record PostNLCredentials(string ApiKey, string CustomerCode, string CustomerNumber, string? CollectionLocation);

    internal static PostNLCredentials Account(PostNLOptions settings, string? account)
    {
        if (account is { Length: > 0 })
        {
            if (!settings.Accounts.TryGetValue(account, out var named) || named.ApiKey is not { Length: > 0 } key
                || named.CustomerCode is not { Length: > 0 } code || named.CustomerNumber is not { Length: > 0 } number)
                throw new CarrierException(CarrierName, $"no credentials for account '{account}': set Spark:Commerce:Shipping:PostNL:Accounts:{account} (user-secrets).");
            return new PostNLCredentials(key, code, number, named.CollectionLocation);
        }
        if (settings.ApiKey is not { Length: > 0 } apiKey || settings.CustomerCode is not { Length: > 0 } customerCode || settings.CustomerNumber is not { Length: > 0 } customerNumber)
            throw new CarrierException(CarrierName, "no credentials: set Spark:Commerce:Shipping:PostNL:ApiKey, CustomerCode and CustomerNumber (user-secrets).");
        return new PostNLCredentials(apiKey, customerCode, customerNumber, settings.CollectionLocation);
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string relative, JsonObject? body, PostNLCredentials account, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, options.CurrentValue.ApiBaseUrl.TrimEnd('/') + "/" + relative);
        request.Headers.TryAddWithoutValidation("apikey", account.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new CarrierException(CarrierName, ErrorMessage(text, (int)response.StatusCode), (int)response.StatusCode);
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        return document.RootElement.Clone();
    }

    /// <summary>PostNL's error shapes: <c>{"Errors":[{"Description"}]}</c>, <c>{"errors":[{"ErrorMsg"}]}</c>, <c>{"Error":{"ErrorDescription"}}</c>, <c>{"message"}</c>.</summary>
    internal static string ErrorMessage(string body, int status)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            foreach (var name in new[] { "Errors", "errors" })
            {
                if (root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
                {
                    var first = list[0];
                    foreach (var field in new[] { "Description", "ErrorMsg", "Message" })
                        if (first.TryGetProperty(field, out var text)) return $"{status} {text.GetString()}";
                }
            }
            if (root.TryGetProperty("Error", out var error) && error.TryGetProperty("ErrorDescription", out var description))
                return $"{status} {description.GetString()}";
            if (root.TryGetProperty("message", out var message))
                return $"{status} {message.GetString()}";
        }
        catch (JsonException) { }
        return $"answered {status}.";
    }

    // ---- Mapping (internal for the fixture tests) ----------------------------------------------------------------

    internal static JsonObject BuildRequest(CarrierShipmentRequest request, PostNLOptions settings, PostNLCredentials account, IReadOnlyList<string>? barcodes, DateTimeOffset now)
    {
        var sender = request.IsReturn ? request.ShipTo : request.ShipFrom;
        var receiver = request.ShipTo;
        var customer = new JsonObject
        {
            ["Address"] = Address("02", sender),
            ["CustomerCode"] = account.CustomerCode,
            ["CustomerNumber"] = account.CustomerNumber,
            ["Name"] = sender.Company ?? sender.Name,
        };
        if (account.CollectionLocation is { Length: > 0 } location)
            customer["CollectionLocation"] = location;

        var reference = request.DangerousGoods.Count > 0 ? "ADR/LQ " + request.Reference : request.Reference;
        var shipments = new JsonArray();
        for (var i = 0; i < request.Parcels.Count; i++)
        {
            var parcel = request.Parcels[i];
            var addresses = new JsonArray { Address("01", receiver) };
            if (request.IsReturn)
                // A return label: the consumer the parcel is collected from is address type 04 (products page).
                addresses.Add(Address("04", request.ShipFrom));
            var shipment = new JsonObject
            {
                ["Addresses"] = addresses,
                ["Dimension"] = Dimension(parcel),
                ["ProductCodeDelivery"] = request.ServiceCode,
                ["Reference"] = reference,
            };
            if (request.ServicePoint is { } point)
            {
                // Delivery to a PostNL location: the location's address as type 09, named by DeliveryAddress.
                addresses.Add(Address("09", new ShippingAddress
                {
                    Company = point.Name, Name = point.Name, Street = point.Address.Street, HouseNumber = point.Address.HouseNumber,
                    PostalCode = point.Address.PostalCode, City = point.Address.City, Country = point.Address.Country,
                }));
                shipment["DeliveryAddress"] = "09";
            }
            if (receiver.Email is { Length: > 0 } || receiver.Phone is { Length: > 0 })
            {
                var contact = new JsonObject { ["ContactType"] = "01" };
                if (receiver.Email is { Length: > 0 } email) contact["Email"] = email;
                if (receiver.Phone is { Length: > 0 } phone) contact["SMSNr"] = phone;
                shipment["Contacts"] = new JsonArray { contact };
            }
            if (barcodes is not null)
            {
                shipment["Barcode"] = barcodes[i];
                shipment["Groups"] = new JsonArray
                {
                    new JsonObject { ["GroupType"] = "03", ["GroupSequence"] = i + 1, ["GroupCount"] = request.Parcels.Count, ["MainBarcode"] = barcodes[0] },
                };
            }
            if (request.Customs is { } customs)
                shipment["Customs"] = Customs(customs);
            shipments.Add(shipment);
        }

        return new JsonObject
        {
            ["Customer"] = customer,
            ["Message"] = new JsonObject
            {
                ["MessageID"] = "1",
                ["MessageTimeStamp"] = SparkTimeZones.ToZone(now, SparkTimeZones.FindOrUtc(StatusZone)).ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture),
                ["Printertype"] = request.LabelFormat == LabelFormat.Zpl ? settings.ZplPrinterType : settings.PdfPrinterType,
            },
            ["Shipments"] = shipments,
        };
    }

    private static JsonObject Address(string type, ShippingAddress address)
    {
        var json = new JsonObject
        {
            ["AddressType"] = type,
            ["City"] = address.City,
            ["Countrycode"] = address.Country,
            ["Street"] = address.Street,
            ["Zipcode"] = address.PostalCode.Replace(" ", string.Empty, StringComparison.Ordinal),
        };
        if (address.HouseNumber is { Length: > 0 } number) json["HouseNr"] = number;
        if (address.Addition is { Length: > 0 } addition) json["HouseNrExt"] = addition;
        if (address.Company is { Length: > 0 } company) json["CompanyName"] = company;
        if (address.Name is { Length: > 0 } name) json["Name"] = name;
        return json;
    }

    /// <summary>Weight in grams; length, width and height in mm, longest side first (schema).</summary>
    private static JsonObject Dimension(ParcelSpec parcel)
    {
        var json = new JsonObject { ["Weight"] = parcel.WeightGrams.ToString(CultureInfo.InvariantCulture) };
        var sides = new[] { parcel.LengthMm, parcel.WidthMm, parcel.HeightMm }.Where(s => s > 0).OrderDescending().ToArray();
        if (sides.Length == 3)
        {
            json["Length"] = sides[0].ToString(CultureInfo.InvariantCulture);
            json["Width"] = sides[1].ToString(CultureInfo.InvariantCulture);
            json["Height"] = sides[2].ToString(CultureInfo.InvariantCulture);
        }
        return json;
    }

    /// <summary>Non-EU customs (schema: <c>Currency</c> EUR or USS, <c>ShipmentType</c> by name, one of invoice/certificate/licence for commercial goods).</summary>
    private static JsonObject Customs(CustomsDeclaration customs)
    {
        var currency = customs.Items.FirstOrDefault()?.Value.Currency ?? "EUR";
        var json = new JsonObject
        {
            ["Currency"] = currency switch
            {
                "EUR" => "EUR",
                "USD" => "USS",
                _ => throw new CarrierException(CarrierName, $"declares customs in EUR or USD only, not {currency}."),
            },
            ["HandleAsNonDeliverable"] = false,
            ["ShipmentType"] = customs.ContentType switch
            {
                CustomsContentType.Gift => "Gift",
                CustomsContentType.Documents => "Documents",
                CustomsContentType.CommercialSample => "Commercial Sample",
                CustomsContentType.ReturnedGoods => "Returned Goods",
                _ => "Commercial Goods",
            },
            ["Content"] = new JsonArray(customs.Items.Select(item => (JsonNode)new JsonObject
            {
                ["Description"] = item.Description.Length > 35 ? item.Description[..35] : item.Description,
                ["Quantity"] = item.Quantity,
                ["Weight"] = item.WeightGrams,
                ["Value"] = Math.Round(item.Value.Amount, 2, MidpointRounding.AwayFromZero),
                ["HSTariffNr"] = item.HsCode,
                ["CountryOfOrigin"] = item.OriginCountry,
            }).ToArray()),
        };
        if (customs.InvoiceNumber is { Length: > 0 } invoice)
        {
            json["Invoice"] = true;
            json["InvoiceNr"] = invoice;
        }
        else
            json["Invoice"] = false;
        return json;
    }

    internal static CarrierShipmentResult ReadResponse(JsonElement response, int expected)
    {
        if (!response.TryGetProperty("ResponseShipments", out var shipments) || shipments.ValueKind != JsonValueKind.Array)
            throw new CarrierException(CarrierName, "answered no ResponseShipments.");
        var parcels = new List<CarrierParcelResult>();
        CarrierDocument? customs = null;
        foreach (var shipment in shipments.EnumerateArray())
        {
            if (shipment.TryGetProperty("Errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                throw new CarrierException(CarrierName, "refused the shipment: " + errors.GetRawText());
            var barcode = shipment.GetProperty("Barcode").GetString() ?? string.Empty;
            CarrierDocument? label = null;
            if (shipment.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in labels.EnumerateArray())
                {
                    var type = entry.TryGetProperty("Labeltype", out var t) ? t.GetString() : null;
                    var document = Document(entry);
                    if (string.Equals(type, "Label", StringComparison.OrdinalIgnoreCase) || (label is null && type is null))
                        label ??= document;
                    else if (type is "CN23" or "CommercialInvoice" or "CP71")
                        customs ??= document;
                }
            }
            parcels.Add(new CarrierParcelResult(barcode, barcode, TrackingUrl(barcode), label
                ?? throw new CarrierException(CarrierName, $"answered no label for {barcode} (merged labels are not requested).")));
        }
        if (parcels.Count != expected)
            throw new CarrierException(CarrierName, $"answered {parcels.Count} shipments for {expected} parcels.");
        return new CarrierShipmentResult(parcels[0].CarrierParcelId, parcels, customs);
    }

    private static CarrierDocument Document(JsonElement label)
    {
        var bytes = Convert.FromBase64String(label.GetProperty("Content").GetString() ?? string.Empty);
        var output = label.TryGetProperty("OutputType", out var o) ? o.GetString() : "PDF";
        return string.Equals(output, "ZPL", StringComparison.OrdinalIgnoreCase)
            ? new CarrierDocument(bytes, CarrierDocument.Zpl, 203)
            : string.Equals(output, "PNG", StringComparison.OrdinalIgnoreCase) ? new CarrierDocument(bytes, CarrierDocument.Png) : new CarrierDocument(bytes, CarrierDocument.Pdf);
    }

    private static string TrackingUrl(string barcode) => "https://jouw.postnl.nl/track-and-trace/" + Uri.EscapeDataString(barcode);

    /// <summary>
    /// The current status of <c>/shipment/v2/status/barcode/{barcode}</c>. <c>Shipment</c> is an object, or an array
    /// for multi-collo; empty values come back as <c>{}</c>; <c>TimeStamp</c> is <c>dd-MM-yyyy HH:mm:ss</c> Dutch time.
    /// </summary>
    internal static IReadOnlyList<CarrierTrackingEvent> ReadStatus(JsonElement response)
    {
        var root = response.TryGetProperty("CurrentStatus", out var current) ? current
            : response.TryGetProperty("CompleteStatus", out var complete) ? complete : default;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Shipment", out var shipment))
            return [];
        IEnumerable<JsonElement> list = shipment.ValueKind == JsonValueKind.Array ? shipment.EnumerateArray() : [shipment];
        var events = new List<CarrierTrackingEvent>();
        foreach (var item in list)
        {
            if (Text(item, "Barcode") is not { Length: > 0 } barcode || !item.TryGetProperty("Status", out var status) || status.ValueKind != JsonValueKind.Object)
                continue;
            var code = Text(status, "StatusCode") ?? "99";
            var stamp = Text(status, "TimeStamp");
            var at = ParseTime(stamp) ?? DateTimeOffset.UnixEpoch;
            var id = "pn_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{barcode}|{code}|{stamp}")))[..32];
            events.Add(new CarrierTrackingEvent(id, barcode, barcode, MapStatus(code), code, Text(status, "StatusDescription"), at));
        }
        return events;
    }

    /// <summary>The T&amp;T status codes (reference page, phases 1-4 and 99).</summary>
    internal static TrackingStatus MapStatus(string code) => code switch
    {
        "1" or "13" => TrackingStatus.Labelled,
        "2" or "3" or "5" or "6" or "9" or "20" or "21" => TrackingStatus.InTransit,
        "7" => TrackingStatus.OutForDelivery,
        "11" or "22" or "23" => TrackingStatus.Delivered,
        "12" => TrackingStatus.AvailableForPickup,
        "4" or "8" or "14" or "15" or "16" or "17" or "18" or "19" => TrackingStatus.Exception,
        _ => TrackingStatus.InTransit,
    };

    internal static DateTimeOffset? ParseTime(string? stamp)
    {
        if (stamp is not { Length: > 0 })
            return null;
        if (!DateTime.TryParseExact(stamp, ["dd-MM-yyyy HH:mm:ss", "dd-MM-yyyy HH:mm:ss.fff"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var iso) ? iso : null;
        return SparkTimeZones.ToInstant(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), SparkTimeZones.FindOrUtc(StatusZone));
    }

    internal static IEnumerable<ServicePoint> ReadLocations(JsonElement response)
    {
        if (!response.TryGetProperty("GetLocationsResult", out var result) || !result.TryGetProperty("ResponseLocation", out var locations))
            yield break;
        IEnumerable<JsonElement> list = locations.ValueKind == JsonValueKind.Array ? locations.EnumerateArray() : [locations];
        foreach (var location in list)
        {
            var address = location.TryGetProperty("Address", out var a) ? a : default;
            var options = location.TryGetProperty("DeliveryOptions", out var d) && d.TryGetProperty("string", out var s) ? Strings(s) : [];
            var hours = new Dictionary<string, IReadOnlyList<string>>();
            if (location.TryGetProperty("OpeningHours", out var opening) && opening.ValueKind == JsonValueKind.Object)
            {
                foreach (var day in opening.EnumerateObject())
                {
                    if (day.Value.ValueKind == JsonValueKind.Object && day.Value.TryGetProperty("string", out var times))
                        hours[day.Name.ToLowerInvariant()] = Strings(times);
                }
            }
            yield return new ServicePoint
            {
                Id = Text(location, "LocationCode") ?? string.Empty,
                Carrier = CarrierName,
                Name = Text(location, "Name") ?? string.Empty,
                Kind = options.Contains("PA") && !options.Contains("PG") ? ServicePointKind.ParcelLocker : ServicePointKind.PickupPoint,
                Address = new ShippingAddress
                {
                    Name = Text(location, "Name") ?? string.Empty,
                    Street = Text(address, "Street") ?? string.Empty,
                    HouseNumber = Text(address, "HouseNr"),
                    Addition = Text(address, "HouseNrExt"),
                    PostalCode = Text(address, "Zipcode") ?? string.Empty,
                    City = Text(address, "City") ?? string.Empty,
                    Country = Text(address, "Countrycode") ?? string.Empty,
                },
                Latitude = Number(location, "Latitude"),
                Longitude = Number(location, "Longitude"),
                DistanceMetres = Number(location, "Distance") is { } distance ? (int)distance : null,
                OpeningHours = hours,
            };
        }
    }

    private static List<string> Strings(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToList(),
        JsonValueKind.String => [element.GetString()!],
        _ => [],
    };

    /// <summary>A string or number; PostNL's empty <c>{}</c> reads as null.</summary>
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
}
