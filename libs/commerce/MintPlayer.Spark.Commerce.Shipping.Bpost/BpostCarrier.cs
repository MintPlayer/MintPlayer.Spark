using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Shipping.Bpost;

/// <summary>
/// <c>Spark:Commerce:Shipping:Bpost</c>. Credentials come from configuration or user-secrets only, are read per call
/// and never logged.
/// </summary>
public sealed class BpostOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Shipping:Bpost";

    /// <summary>The Shipping Manager account id.</summary>
    public string? AccountId { get; set; }

    /// <summary>The Shipping Manager passphrase (HTTP Basic password).</summary>
    public string? PassPhrase { get; set; }

    /// <summary>Further accounts by name (warehouse WD8).</summary>
    public Dictionary<string, BpostAccount> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Overrides of the shipped products, and further products (dangerous goods: GC9).</summary>
    public Dictionary<string, CarrierServiceOptions> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cost centre printed on the order, if any.</summary>
    public string? CostCenter { get; set; }

    /// <summary>The label size: <c>A6</c> or <c>A4</c>.</summary>
    public string LabelSize { get; set; } = "A6";

    /// <summary>The pick-up point locator's partner id (from bpost).</summary>
    public string? LocatorPartner { get; set; }

    /// <summary>The locator's application id, when bpost issued one.</summary>
    public string? LocatorAppId { get; set; }

    /// <summary>The locator's language: <c>nl</c> or <c>fr</c>.</summary>
    public string LocatorLanguage { get; set; } = "nl";

    /// <summary>The Shipping Manager base. bpost's WooCommerce guide names <c>https://api.bpost.be/services/shm/</c> instead.</summary>
    public string ApiBaseUrl { get; set; } = "https://shm-rest.bpost.cloud/services/shm/";

    /// <summary>The pick-up point locator.</summary>
    public string LocatorUrl { get; set; } = "https://pudo.bpost.be/Locator";
}

/// <summary>One bpost account.</summary>
public sealed class BpostAccount
{
    public string? AccountId { get; set; }

    public string? PassPhrase { get; set; }
}

/// <summary>Registration of the bpost adapter.</summary>
public static class BpostShippingExtensions
{
    /// <summary>
    /// Adds bpost (<c>bpost</c>): Shipping Manager orders and labels, pick-up points and parcel lockers, and tracking by
    /// polling the order's box statuses (bpost publishes no tracking webhook). Credentials:
    /// <c>Spark:Commerce:Shipping:Bpost</c> (user-secrets).
    /// </summary>
    public static SparkShippingBuilder UseBpost(this SparkShippingBuilder shipping, Action<BpostOptions>? configure = null)
    {
        var builder = shipping.Spark;
        builder.Services.AddSparkConfigurationSection<BpostOptions>();
        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddHttpClient(BpostCarrier.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        builder.AddServicePointProvider<BpostCarrier>();
        return builder.AddShippingCarrier<BpostCarrier>(BpostCarrier.CarrierName);
    }
}

/// <summary>
/// bpost (D14). bpost publishes no public API reference; this adapter follows the request and response shapes of the
/// shipping-manager library bpost's own e-commerce plugins use (<c>Antidot-be/bpost-api-library</c>), and its tests
/// use that library's example fixtures, marked SDK-derived (D37). A shipment is one order with one box per parcel
/// (GB5); labels come from the order's label call; the customs information of international boxes carries HS code and
/// origin per line (GB6). Unconfirmed: the value unit (read as eurocents), the parcel-locker (<c>at247</c>) fields,
/// and how a return product's addresses are read.
/// </summary>
internal sealed partial class BpostCarrier(IHttpClientFactory httpClients, IOptionsMonitor<BpostOptions> options, TimeProvider time) : IShippingCarrier, IServicePointProvider, IShippingBookingLookup
{
    public const string CarrierName = "bpost";
    public const string HttpClientName = "spark-commerce-shipping-bpost";

    private static readonly XNamespace Tns = "http://schema.post.be/shm/deepintegration/v5/";
    private static readonly XNamespace National = "http://schema.post.be/shm/deepintegration/v5/national";
    private static readonly XNamespace Common = "http://schema.post.be/shm/deepintegration/v5/common";
    private static readonly XNamespace International = "http://schema.post.be/shm/deepintegration/v5/international";
    private static readonly XNamespace V3 = "http://schema.post.be/shm/deepintegration/v3/";

    public string Name => CarrierName;

    string IServicePointProvider.Carrier => CarrierName;

    public CarrierCapabilities Capabilities { get; } = new()
    {
        LabelFormats = [LabelFormat.Pdf],
        VoidLabel = true,
        // The Shipping Manager cancels per order: every box of the booking.
        VoidsWholeShipment = true,
        ReturnLabels = true,
        MultiParcel = true,
        ServicePoints = true,
        CustomsDocuments = true,
        DangerousGoodsMarks = false,
        TrackingWebhooks = false,
        TrackingPull = true,
    };

    /// <summary>The products of the library's constants. Weights and destinations are bpost's published limits only where stated; confirm with the contract.</summary>
    internal static readonly IReadOnlyList<CarrierService> Shipped =
    [
        new("bpack 24h Pro", "bpost home delivery") { Countries = ["BE"], FromCountries = ["BE"], MaxWeightGrams = 30_000 },
        new("bpack@bpost", "bpost pick-up point") { Countries = ["BE"], FromCountries = ["BE"], MaxWeightGrams = 30_000, ServicePoint = true },
        new("bpack 24/7", "bpost parcel locker") { Countries = ["BE"], FromCountries = ["BE"], MaxWeightGrams = 30_000, ServicePoint = true },
        new("bpack Easy Retour", "bpost return") { Countries = ["BE"], FromCountries = ["BE"], MaxWeightGrams = 30_000, Return = true },
        new("bpack Europe Business", "bpost Europe") { FromCountries = ["BE"], MaxWeightGrams = 30_000 },
        new("bpack World Express Pro", "bpost world express") { FromCountries = ["BE"], MaxWeightGrams = 30_000, CustomsStyle = CustomsDocumentStyle.PostalParcel },
    ];

    public IReadOnlyList<CarrierService> Services => CarrierCatalog.Apply(Shipped, options.CurrentValue.Services);

    public async Task<CarrierShipmentResult> CreateShipmentAsync(CarrierShipmentRequest request, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var account = Account(settings, request.Account);
        var reference = OrderReference(request.ShipmentId);
        var order = BuildOrder(request, account.AccountId, reference, settings.CostCenter);
        await SendAsync(HttpMethod.Post, $"{Uri.EscapeDataString(account.AccountId)}/orders", order.ToString(SaveOptions.DisableFormatting),
            "application/vnd.bpost.shm-order-v5+XML", null, account, cancellationToken);
        var labels = await SendAsync(HttpMethod.Post, $"{Uri.EscapeDataString(account.AccountId)}/orders/{Uri.EscapeDataString(reference)}/labels/{(settings.LabelSize == "A4" ? "A4" : "A6")}",
            null, "application/vnd.bpost.shm-labelRequest-v3+XML", "application/vnd.bpost.shm-label-pdf-v3.4+XML", account, cancellationToken);
        return ReadLabels(XDocument.Parse(labels), reference, request.Parcels.Count);
    }

    /// <summary>
    /// The order of the booking's reference (<c>GET /{account}/orders/{reference}</c>): absent (404), nothing was booked;
    /// present, its labels are created again from the stored order (the label call is repeatable), so a lost answer
    /// never books a second order.
    /// </summary>
    public async Task<CarrierBookingLookup> FindBookingAsync(CarrierShipmentRequest request, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var account = Account(settings, request.Account);
        var reference = OrderReference(request.ShipmentId);
        try
        {
            await SendAsync(HttpMethod.Get, $"{Uri.EscapeDataString(account.AccountId)}/orders/{Uri.EscapeDataString(reference)}", null, null,
                "application/vnd.bpost.shm-order-v3.5+XML", account, cancellationToken);
        }
        catch (CarrierException ex) when (ex.StatusCode == 404)
        {
            return CarrierBookingLookup.NotFound;
        }
        var labels = await SendAsync(HttpMethod.Post, $"{Uri.EscapeDataString(account.AccountId)}/orders/{Uri.EscapeDataString(reference)}/labels/{(settings.LabelSize == "A4" ? "A4" : "A6")}",
            null, "application/vnd.bpost.shm-labelRequest-v3+XML", "application/vnd.bpost.shm-label-pdf-v3.4+XML", account, cancellationToken);
        return CarrierBookingLookup.Found(ReadLabels(XDocument.Parse(labels), reference, request.Parcels.Count));
    }

    /// <summary>Cancels the whole order (the Shipping Manager's status update works per order): every parcel of the shipment.</summary>
    public async Task<bool> VoidAsync(CarrierParcelReference parcel, CancellationToken cancellationToken)
    {
        var account = Account(options.CurrentValue, parcel.Account);
        var reference = parcel.CarrierParcelId.Split(':')[0];
        var body = new XElement(V3 + "orderUpdate", new XElement(V3 + "status", "CANCELLED")).ToString(SaveOptions.DisableFormatting);
        try
        {
            await SendAsync(HttpMethod.Post, $"{Uri.EscapeDataString(account.AccountId)}/orders/{Uri.EscapeDataString(reference)}", body,
                "application/vnd.bpost.shm-orderUpdate-v3+XML", null, account, cancellationToken);
            return true;
        }
        catch (CarrierException ex) when (ex.StatusCode is 400 or 404 or 409)
        {
            return false;
        }
    }

    /// <summary>The box's status from the fetched order (<c>ANNOUNCED</c>, <c>IN_TRANSIT</c>, <c>DELIVERED</c>, …). bpost publishes no tracking API.</summary>
    public async Task<IReadOnlyList<CarrierTrackingEvent>> GetTrackingAsync(CarrierParcelReference parcel, CancellationToken cancellationToken)
    {
        var account = Account(options.CurrentValue, parcel.Account);
        var reference = parcel.CarrierParcelId.Split(':')[0];
        var xml = await SendAsync(HttpMethod.Get, $"{Uri.EscapeDataString(account.AccountId)}/orders/{Uri.EscapeDataString(reference)}", null, null,
            "application/vnd.bpost.shm-order-v3.5+XML", account, cancellationToken);
        return ReadOrderStatus(XDocument.Parse(xml), time.GetUtcNow()).Where(e => e.TrackingNumber == parcel.TrackingNumber).ToList();
    }

    public Task<IReadOnlyList<CarrierTrackingEvent>> ParseWebhookAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CarrierTrackingEvent>>([]);

    public async Task<IReadOnlyList<ServicePoint>> SearchAsync(ServicePointQuery query, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        if (query.Country != "BE" || settings.LocatorPartner is not { Length: > 0 } partner)
            return [];
        // Type bitmask: 1 post office, 2 post point, 4 bpack 24/7.
        var type = query.Kind switch { ServicePointKind.ParcelLocker => 4, ServicePointKind.PickupPoint => 3, _ => 7 };
        var parameters = new List<string>
        {
            "Function=search", "Format=xml", "Country=BE",
            "Partner=" + Uri.EscapeDataString(partner),
            "Language=" + (settings.LocatorLanguage == "fr" ? "fr" : "nl"),
            "Zone=" + Uri.EscapeDataString(query.PostalCode),
            "Type=" + type.ToString(CultureInfo.InvariantCulture),
            "Limit=" + query.Limit.ToString(CultureInfo.InvariantCulture),
        };
        if (settings.LocatorAppId is { Length: > 0 } appId) parameters.Add("AppId=" + Uri.EscapeDataString(appId));
        if (query.Street is { Length: > 0 } street) parameters.Add("Street=" + Uri.EscapeDataString(street));
        if (query.HouseNumber is { Length: > 0 } number) parameters.Add("Number=" + Uri.EscapeDataString(number));
        using var response = await httpClients.CreateClient(HttpClientName).GetAsync(settings.LocatorUrl + "?" + string.Join('&', parameters), cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new CarrierException(CarrierName, $"the locator answered {(int)response.StatusCode}.", (int)response.StatusCode);
        return ReadLocator(XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken))).ToList();
    }

    // ---- HTTP ----------------------------------------------------------------------------------------------------

    internal sealed record BpostCredentials(string AccountId, string PassPhrase);

    internal static BpostCredentials Account(BpostOptions settings, string? account)
    {
        if (account is { Length: > 0 })
        {
            if (!settings.Accounts.TryGetValue(account, out var named) || named.AccountId is not { Length: > 0 } id || named.PassPhrase is not { Length: > 0 } pass)
                throw new CarrierException(CarrierName, $"no credentials for account '{account}': set Spark:Commerce:Shipping:Bpost:Accounts:{account} (user-secrets).");
            return new BpostCredentials(id, pass);
        }
        if (settings.AccountId is not { Length: > 0 } accountId || settings.PassPhrase is not { Length: > 0 } passPhrase)
            throw new CarrierException(CarrierName, "no credentials: set Spark:Commerce:Shipping:Bpost:AccountId and PassPhrase (user-secrets).");
        return new BpostCredentials(accountId, passPhrase);
    }

    private async Task<string> SendAsync(HttpMethod method, string relative, string? body, string? contentType, string? accept, BpostCredentials account, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, options.CurrentValue.ApiBaseUrl.TrimEnd('/') + "/" + relative);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account.AccountId}:{account.PassPhrase}")));
        if (accept is not null)
            request.Headers.TryAddWithoutValidation("Accept", accept);
        if (contentType is not null)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body ?? string.Empty));
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new CarrierException(CarrierName, $"answered {(int)response.StatusCode}: {ErrorMessage(text)}", (int)response.StatusCode);
        return text;
    }

    /// <summary>bpost's error body (<c>&lt;invalidSelection&gt;</c>/<c>&lt;error&gt;</c> text), cut short.</summary>
    private static string ErrorMessage(string body)
    {
        try
        {
            var text = XDocument.Parse(body).Root?.Value ?? string.Empty;
            return text.Length > 300 ? text[..300] : text;
        }
        catch (System.Xml.XmlException) { return body.Length > 300 ? body[..300] : body; }
    }

    // ---- Mapping (internal for the fixture tests) ----------------------------------------------------------------

    /// <summary>An order reference from our shipment id: letters, digits, <c>-</c> and <c>_</c>, at most 50.</summary>
    internal static string OrderReference(string shipmentId)
    {
        var cleaned = ReferenceCleaner().Replace(shipmentId, "-").Trim('-');
        return cleaned.Length <= 50 ? cleaned : cleaned[^50..];
    }

    internal static XDocument BuildOrder(CarrierShipmentRequest request, string accountId, string reference, string? costCenter)
    {
        var order = new XElement(Tns + "order",
            new XAttribute("xmlns", National.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "tns", Tns.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "common", Common.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "international", International.NamespaceName),
            new XElement(Tns + "accountId", accountId),
            new XElement(Tns + "reference", reference));
        if (costCenter is { Length: > 0 })
            order.Add(new XElement(Tns + "costCenter", costCenter));
        foreach (var line in request.Customs?.Items ?? [])
            order.Add(new XElement(Tns + "orderLine", new XElement(Tns + "text", Cut(line.Description, 50)), new XElement(Tns + "nbOfItems", line.Quantity)));

        var sender = request.ShipFrom;
        var receiver = request.ShipTo;
        var international = request.Customs is not null || receiver.Country != "BE";
        for (var i = 0; i < request.Parcels.Count; i++)
        {
            var parcel = request.Parcels[i];
            var box = new XElement(Tns + "box", new XElement(Tns + "sender", Party(sender)));
            if (international)
            {
                var content = new XElement(International + "international",
                    new XElement(International + "product", request.ServiceCode),
                    new XElement(International + "receiver", Party(receiver)),
                    new XElement(International + "parcelWeight", parcel.WeightGrams));
                if (request.Customs is { } customs)
                {
                    var currency = customs.Items.FirstOrDefault()?.Value.Currency ?? "EUR";
                    content.Add(new XElement(International + "customsInfo",
                        new XElement(International + "parcelValue", Cents(customs.TotalValue(currency).Amount)),
                        new XElement(International + "contentDescription", Cut(string.Join(", ", customs.Items.Select(c => c.Description)), 50)),
                        new XElement(International + "shipmentType", customs.ContentType switch
                        {
                            CustomsContentType.Gift => "GIFT",
                            CustomsContentType.Documents => "DOCUMENTS",
                            CustomsContentType.CommercialSample => "SAMPLE",
                            CustomsContentType.ReturnedGoods => "OTHER",
                            _ => "GOODS",
                        }),
                        new XElement(International + "parcelReturnInstructions", "RTS"),
                        new XElement(International + "privateAddress", receiver.Company is { Length: > 0 } ? "false" : "true"),
                        new XElement(International + "currency", currency)));
                    content.Add(new XElement(International + "parcelContents", customs.Items.Select(item => new XElement(International + "parcelContent",
                        new XElement(International + "numberOfItemType", item.Quantity),
                        new XElement(International + "valueOfItem", Cents(item.Value.Amount)),
                        new XElement(International + "itemDescription", Cut(item.Description, 30)),
                        new XElement(International + "nettoWeight", item.WeightGrams),
                        new XElement(International + "hsTariffCode", item.HsCode),
                        new XElement(International + "originOfGoods", item.OriginCountry)))));
                }
                box.Add(new XElement(Tns + "internationalBox", content));
            }
            else
                box.Add(new XElement(Tns + "nationalBox", NationalDelivery(request, receiver, parcel)));
            box.Add(new XElement(Tns + "additionalCustomerReference", Cut(request.Reference, 50)));
            order.Add(box);
        }
        return new XDocument(order);
    }

    private static XElement NationalDelivery(CarrierShipmentRequest request, ShippingAddress receiver, ParcelSpec parcel)
    {
        var receiverName = Cut(receiver.Name, 40);
        if (request.ServicePoint is { } point && request.ServiceCode == "bpack 24/7")
            return new XElement(National + "at247",
                new XElement(National + "product", request.ServiceCode),
                new XElement(National + "weight", parcel.WeightGrams),
                new XElement(National + "parcelsDepotId", point.Id),
                new XElement(National + "parcelsDepotName", Cut(point.Name, 40)),
                new XElement(National + "parcelsDepotAddress", Address(point.Address)),
                new XElement(National + "receiverName", receiverName));
        if (request.ServicePoint is { } pugo)
            return new XElement(National + "atBpost",
                new XElement(National + "product", request.ServiceCode),
                new XElement(National + "weight", parcel.WeightGrams),
                new XElement(National + "pugoId", pugo.Id),
                new XElement(National + "pugoName", Cut(pugo.Name, 40)),
                new XElement(National + "pugoAddress", Address(pugo.Address)),
                new XElement(National + "receiverName", receiverName));
        return new XElement(National + "atHome",
            new XElement(National + "product", request.ServiceCode),
            new XElement(National + "weight", parcel.WeightGrams),
            new XElement(National + "receiver", Party(receiver)));
    }

    private static IEnumerable<XElement> Party(ShippingAddress address)
    {
        yield return new XElement(Common + "name", Cut(address.Name, 40));
        if (address.Company is { Length: > 0 } company)
            yield return new XElement(Common + "company", Cut(company, 40));
        yield return new XElement(Common + "address", Address(address));
        if (address.Email is { Length: > 0 } email)
            yield return new XElement(Common + "emailAddress", email);
        if (address.Phone is { Length: > 0 } phone)
            yield return new XElement(Common + "phoneNumber", phone);
    }

    private static IEnumerable<XElement> Address(ShippingAddress address)
    {
        yield return new XElement(Common + "streetName", Cut(address.Street, 40));
        if (address.HouseNumber is { Length: > 0 } number)
            yield return new XElement(Common + "number", Cut(number, 8));
        if (address.Addition is { Length: > 0 } box)
            yield return new XElement(Common + "box", Cut(box, 8));
        yield return new XElement(Common + "postalCode", address.PostalCode);
        yield return new XElement(Common + "locality", Cut(address.City, 40));
        yield return new XElement(Common + "countryCode", address.Country);
    }

    /// <summary>
    /// The label response: <c>labels/label</c> with one or more <c>barcodeWithReference/barcode</c>, a <c>mimeType</c>
    /// and base64 <c>bytes</c>. A label that holds several boxes' barcodes (one PDF for the order) is given to each.
    /// </summary>
    internal static CarrierShipmentResult ReadLabels(XDocument document, string reference, int expected)
    {
        var parcels = new List<CarrierParcelResult>();
        foreach (var label in document.Descendants().Where(e => e.Name.LocalName == "label"))
        {
            var mediaType = label.Elements().FirstOrDefault(e => e.Name.LocalName == "mimeType")?.Value ?? CarrierDocument.Pdf;
            var bytes = Convert.FromBase64String(label.Elements().FirstOrDefault(e => e.Name.LocalName == "bytes")?.Value ?? string.Empty);
            var document_ = new CarrierDocument(bytes, mediaType == "image/png" ? CarrierDocument.Png : CarrierDocument.Pdf);
            foreach (var barcode in label.Descendants().Where(e => e.Name.LocalName == "barcode").Select(e => e.Value.Trim()).Where(b => b.Length > 0))
            {
                if (parcels.Any(p => p.TrackingNumber == barcode))
                    continue;
                parcels.Add(new CarrierParcelResult($"{reference}:{barcode}", barcode, TrackingUrl(barcode), document_));
            }
        }
        if (parcels.Count != expected)
            throw new CarrierException(CarrierName, $"answered labels for {parcels.Count} boxes, expected {expected}.");
        return new CarrierShipmentResult(reference, parcels);
    }

    private static string TrackingUrl(string barcode) => "https://track.bpost.cloud/btr/web/#/search?itemCode=" + Uri.EscapeDataString(barcode);

    /// <summary>The fetched order's boxes (<c>box/barcode</c>, <c>box/status</c>). bpost states no time, so the poll's time is the event's.</summary>
    internal static IReadOnlyList<CarrierTrackingEvent> ReadOrderStatus(XDocument document, DateTimeOffset now)
    {
        var events = new List<CarrierTrackingEvent>();
        foreach (var box in document.Descendants().Where(e => e.Name.LocalName == "box"))
        {
            var barcode = box.Elements().FirstOrDefault(e => e.Name.LocalName == "barcode")?.Value.Trim();
            var status = box.Elements().FirstOrDefault(e => e.Name.LocalName == "status")?.Value.Trim();
            if (barcode is not { Length: > 0 } || status is not { Length: > 0 })
                continue;
            // The id is the (box, status) pair: polling the same status again adds nothing.
            var id = "bp_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{barcode}|{status}")))[..32];
            events.Add(new CarrierTrackingEvent(id, barcode, null, MapStatus(status), status, status, now));
        }
        return events;
    }

    /// <summary>The box statuses of the library (<c>Box::BOX_STATUS_*</c>).</summary>
    internal static TrackingStatus MapStatus(string status) => status switch
    {
        "OPEN" or "PENDING" or "ON-HOLD" => TrackingStatus.Pending,
        "PRINTED" or "ANNOUNCED" => TrackingStatus.Labelled,
        "IN_TRANSIT" => TrackingStatus.InTransit,
        "AWAITING_PICKUP" => TrackingStatus.AvailableForPickup,
        "DELIVERED" => TrackingStatus.Delivered,
        "BACK_TO_SENDER" => TrackingStatus.ReturnedToSender,
        "CANCELLED" => TrackingStatus.Cancelled,
        _ => TrackingStatus.InTransit,
    };

    /// <summary>The locator's <c>PoiList/Poi/Record</c> (Id, Type, Name, Street, Number, Zip, City, Latitude, Longitude) and <c>Distance</c>.</summary>
    internal static IEnumerable<ServicePoint> ReadLocator(XDocument document)
    {
        foreach (var poi in document.Descendants().Where(e => e.Name.LocalName == "Poi"))
        {
            var record = poi.Elements().FirstOrDefault(e => e.Name.LocalName == "Record");
            if (record is null)
                continue;
            string? Field(string name) => record.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value.Trim();
            double? Coordinate(string name) => double.TryParse(Field(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            var distance = double.TryParse(poi.Elements().FirstOrDefault(e => e.Name.LocalName == "Distance")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var metres) ? (int?)Math.Round(metres) : null;
            var name = Field("Name") ?? string.Empty;
            yield return new ServicePoint
            {
                Id = Field("Id") ?? string.Empty,
                Carrier = CarrierName,
                Name = name,
                Kind = Field("Type") == "4" ? ServicePointKind.ParcelLocker : ServicePointKind.PickupPoint,
                Address = new ShippingAddress
                {
                    Name = name,
                    Street = Field("Street") ?? string.Empty,
                    HouseNumber = Field("Number"),
                    PostalCode = Field("Zip") ?? string.Empty,
                    City = Field("City") ?? string.Empty,
                    Country = Field("Country") ?? "BE",
                },
                Latitude = Coordinate("Latitude"),
                Longitude = Coordinate("Longitude"),
                DistanceMetres = distance,
            };
        }
    }

    /// <summary>Euro amounts in cents (the library's fixture declares 1100 for items of 200 and 900; inferred, not confirmed).</summary>
    private static string Cents(decimal amount) => Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);

    private static string Cut(string? value, int max) => value is null ? string.Empty : value.Length <= max ? value : value[..max];

    [GeneratedRegex("[^A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceCleaner();
}
