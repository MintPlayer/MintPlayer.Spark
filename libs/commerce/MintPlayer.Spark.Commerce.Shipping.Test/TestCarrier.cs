using MintPlayer.Spark.Commerce.Shipping.Services;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Shipping.Test;

/// <summary><c>Spark:Commerce:Shipping:Test</c>.</summary>
public sealed class TestShippingOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Shipping:Test";

    /// <summary>Allow the test carrier in Production (a demo). Off: startup is refused there.</summary>
    public bool AllowInProduction { get; set; }

    /// <summary>The ZPL resolution the test carrier states.</summary>
    public int ZplDpi { get; set; } = 203;
}

/// <summary>
/// The in-process test carrier (D14): every capability, so every path of Shipping can run without a carrier
/// account (GB5 service points, multi-parcel and return labels; GB6 customs; GC9 dangerous goods). Its services:
/// <list type="bullet">
/// <item><c>test-standard</c>: anywhere, up to 31.5 kg, no dangerous goods, postal parcel (CN23);</item>
/// <item><c>test-dg</c>: like standard, accepting lithium-ion and lithium-metal batteries in or with equipment and limited quantities;</item>
/// <item><c>test-point</c>: to a service point or locker, BE/NL/LU/FR/DE;</item>
/// <item><c>test-letter</c>: letter post up to 2 kg (CN22/CN23 by value);</item>
/// <item><c>test-express</c>: express with a commercial invoice;</item>
/// <item><c>test-return</c>: returns from anywhere.</item>
/// </list>
/// </summary>
public sealed class TestCarrier(TestCarrierStore store, IOptionsMonitor<TestShippingOptions> options, TimeProvider time) : IShippingCarrier, IServicePointProvider, IShippingBookingLookup
{
    public const string CarrierName = "test";

    public string Name => CarrierName;

    string IServicePointProvider.Carrier => CarrierName;

    public CarrierCapabilities Capabilities { get; } = new()
    {
        LabelFormats = [LabelFormat.Pdf, LabelFormat.Zpl],
        VoidLabel = true,
        ReturnLabels = true,
        MultiParcel = true,
        ServicePoints = true,
        CustomsDocuments = true,
        DangerousGoodsMarks = true,
        TrackingWebhooks = true,
        TrackingPull = true,
    };

    public IReadOnlyList<CarrierService> Services { get; } =
    [
        new("test-standard", "Test standard") { MaxWeightGrams = 31_500 },
        new("test-dg", "Test dangerous goods")
        {
            MaxWeightGrams = 31_500,
            AcceptedDangerousGoods =
            [
                DangerousGoodsClass.LithiumIonInEquipment, DangerousGoodsClass.LithiumIonPackedWithEquipment,
                DangerousGoodsClass.LithiumMetalInEquipment, DangerousGoodsClass.LithiumMetalPackedWithEquipment,
                DangerousGoodsClass.LimitedQuantity,
            ],
        },
        new("test-point", "Test service point") { MaxWeightGrams = 20_000, ServicePoint = true, Countries = ["BE", "NL", "LU", "FR", "DE"] },
        new("test-letter", "Test letter post") { MaxWeightGrams = 2_000, CustomsStyle = CustomsDocumentStyle.LetterPost },
        new("test-express", "Test express") { MaxWeightGrams = 31_500, CustomsStyle = CustomsDocumentStyle.CommercialInvoice },
        new("test-return", "Test return") { MaxWeightGrams = 31_500, Return = true },
    ];

    public Task<CarrierShipmentResult> CreateShipmentAsync(CarrierShipmentRequest request, CancellationToken cancellationToken)
    {
        if (request.Parcels.Count == 0)
            throw new CarrierException(CarrierName, "no parcels.");
        var marks = DangerousGoodsRules.Marks(request.DangerousGoods);
        var zpl = request.LabelFormat == LabelFormat.Zpl;
        var shipmentId = "tsh_" + store.Next();
        var parcels = new List<CarrierParcelResult>();
        foreach (var (parcel, index) in request.Parcels.Select((p, i) => (p, i + 1)))
        {
            var tracking = "TEST" + store.Next().ToString("D10", CultureInfo.InvariantCulture);
            var lines = new List<string>
            {
                request.IsReturn ? "RETURN" : "TEST CARRIER",
                $"{request.ServiceCode}  {index}/{request.Parcels.Count}",
                tracking,
                request.ShipTo.Name,
                $"{request.ShipTo.Street} {request.ShipTo.HouseNumber}".Trim(),
                $"{request.ShipTo.PostalCode} {request.ShipTo.City} {request.ShipTo.Country}",
                $"{parcel.WeightGrams} g",
                $"Ref {request.Reference}",
            };
            if (request.ServicePointId is { } point)
                lines.Add("Service point " + point);
            lines.AddRange(marks);
            var label = zpl
                ? new CarrierDocument(Encoding.ASCII.GetBytes(TestDocuments.Zpl(lines)), CarrierDocument.Zpl, options.CurrentValue.ZplDpi)
                : new CarrierDocument(TestDocuments.Pdf(lines), CarrierDocument.Pdf);
            var parcelId = "tpc_" + store.Next();
            store.Parcels[tracking] = new TestParcel(parcelId, tracking, request.Reference, request.ServiceCode, request.IsReturn, marks, time.GetUtcNow());
            parcels.Add(new CarrierParcelResult(parcelId, tracking, $"https://track.example.test/{tracking}", label));
        }
        CarrierDocument? customs = null;
        if (request.Customs is { } declaration)
        {
            var lines = new List<string> { declaration.Document.ToString().ToUpperInvariant(), declaration.Incoterm.ToString().ToUpperInvariant(), declaration.ContentType.ToString() };
            lines.AddRange(declaration.Items.Select(i => $"{i.Quantity} x {i.Description} HS {i.HsCode} {i.OriginCountry} {i.Value}"));
            customs = new CarrierDocument(TestDocuments.Pdf(lines), CarrierDocument.Pdf);
        }
        store.Requests.Add(request);
        var result = new CarrierShipmentResult(shipmentId, parcels, customs);
        store.Bookings[request.ShipmentId] = result;
        return Task.FromResult(result);
    }

    /// <summary>The booking made with the request's reference, labels and all, like a carrier's lookup by external reference.</summary>
    public Task<CarrierBookingLookup> FindBookingAsync(CarrierShipmentRequest request, CancellationToken cancellationToken)
        => Task.FromResult(store.Bookings.TryGetValue(request.ShipmentId, out var booked) ? CarrierBookingLookup.Found(booked) : CarrierBookingLookup.NotFound);

    public Task<bool> VoidAsync(CarrierParcelReference parcel, CancellationToken cancellationToken)
    {
        if (!store.Parcels.TryGetValue(parcel.TrackingNumber, out var known) || known.Scanned)
            return Task.FromResult(false);
        store.Parcels[parcel.TrackingNumber] = known with { Voided = true };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<CarrierTrackingEvent>> GetTrackingAsync(CarrierParcelReference parcel, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CarrierTrackingEvent>>(store.Events.Where(e => e.TrackingNumber == parcel.TrackingNumber).ToList());

    public Task<IReadOnlyList<CarrierTrackingEvent>> ParseWebhookAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        using var json = delivery.ReadJson();
        var root = json.RootElement;
        var status = Enum.TryParse<TrackingStatus>(root.GetProperty("status").GetString(), ignoreCase: true, out var parsed) ? parsed : TrackingStatus.Exception;
        IReadOnlyList<CarrierTrackingEvent> events =
        [
            new CarrierTrackingEvent(
                root.GetProperty("id").GetString()!,
                root.GetProperty("trackingNumber").GetString()!,
                root.TryGetProperty("parcelId", out var id) ? id.GetString() : null,
                status,
                status.ToString(),
                root.TryGetProperty("description", out var text) ? text.GetString() : null,
                root.GetProperty("occurredAt").GetDateTimeOffset()),
        ];
        return Task.FromResult(events);
    }

    public Task<IReadOnlyList<ServicePoint>> SearchAsync(ServicePointQuery query, CancellationToken cancellationToken)
    {
        IReadOnlyList<ServicePoint> points = Enumerable.Range(1, Math.Min(query.Limit, 3)).Select(i => new ServicePoint
        {
            Id = $"tsp_{query.Country}_{query.PostalCode}_{i}",
            Carrier = CarrierName,
            Name = i == 3 ? $"Test locker {i}" : $"Test point {i}",
            Kind = i == 3 ? ServicePointKind.ParcelLocker : ServicePointKind.PickupPoint,
            Address = new ShippingAddress { Name = $"Test point {i}", Street = "Teststraat", HouseNumber = i.ToString(CultureInfo.InvariantCulture), PostalCode = query.PostalCode, City = query.City ?? "Testville", Country = query.Country },
            DistanceMetres = i * 350,
            OpeningHours = new Dictionary<string, IReadOnlyList<string>> { ["monday"] = ["09:00-18:00"], ["saturday"] = ["09:00-12:00"] },
        }).ToList();
        return Task.FromResult(points);
    }
}

/// <summary>A parcel the test carrier labelled.</summary>
public sealed record TestParcel(string ParcelId, string TrackingNumber, string Reference, string ServiceCode, bool IsReturn, IReadOnlyList<string> Marks, DateTimeOffset CreatedAt)
{
    public bool Voided { get; init; }

    public bool Scanned { get; init; }
}

/// <summary>The test carrier's state, per process.</summary>
public sealed class TestCarrierStore
{
    private long sequence;

    public ConcurrentDictionary<string, TestParcel> Parcels { get; } = new(StringComparer.Ordinal);

    /// <summary>Every event the simulator announced, for the pull path.</summary>
    public ConcurrentBag<CarrierTrackingEvent> Events { get; } = [];

    /// <summary>Every booking the carrier received (tests read what Shipping sent).</summary>
    public ConcurrentBag<CarrierShipmentRequest> Requests { get; } = [];

    /// <summary>Every booking by the reference it was made with (<see cref="CarrierShipmentRequest.ShipmentId"/>), for the lookup.</summary>
    public ConcurrentDictionary<string, CarrierShipmentResult> Bookings { get; } = new(StringComparer.Ordinal);

    /// <summary>The per-process key the simulator signs its webhooks with (HMAC-SHA256).</summary>
    public string WebhookSecret { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public long Next() => Interlocked.Increment(ref sequence);
}

/// <summary>Minimal documents: a one-page PDF with text lines, and a ZPL label.</summary>
internal static class TestDocuments
{
    public static string Zpl(IEnumerable<string> lines)
    {
        var builder = new StringBuilder("^XA^CI28");
        var y = 30;
        foreach (var line in lines)
        {
            builder.Append(CultureInfo.InvariantCulture, $"^FO30,{y}^A0N,28,28^FD{ZplField(line)}^FS");
            y += 40;
        }
        return builder.Append("^XZ").ToString();
    }

    /// <summary>A valid single-page A6 PDF (Helvetica, ASCII) with one text line per entry.</summary>
    public static byte[] Pdf(IEnumerable<string> lines)
    {
        var content = new StringBuilder("BT /F1 10 Tf 20 400 Td 14 TL");
        foreach (var line in lines)
            content.Append(" (").Append(PdfText(Ascii(line))).Append(") '");
        content.Append(" ET");
        var stream = content.ToString();
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 298 420] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static string Ascii(string value) => new(value.Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());

    /// <summary>ZPL field data cannot hold its command prefixes: they become spaces.</summary>
    private static string ZplField(string value) => value.Replace('^', ' ').Replace('~', ' ');

    /// <summary>A PDF literal string escapes <c>\</c>, <c>(</c> and <c>)</c>.</summary>
    private static string PdfText(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}

/// <summary>
/// Announces tracking changes of test parcels through the real webhook route's receiver
/// (<see cref="IWebhookReceiver"/>): signed, verified, queued on <c>spark-webhooks</c> and deduplicated on the event id,
/// like a real carrier's delivery. Also records each event for the pull path.
/// </summary>
public sealed class TestCarrierSimulator(TestCarrierStore store, IWebhookReceiver receiver, TimeProvider time)
{
    /// <summary>The signature header the test source verifies (HMAC-SHA256 of the body, lower-case hex).</summary>
    public const string SignatureHeader = "X-Spark-Test-Carrier-Signature";

    /// <summary>
    /// Moves a parcel to <paramref name="status"/> at <paramref name="occurredAt"/> (now when null). Duplicate: the same
    /// delivery is sent twice (the receiver drops the second). Answers false for an unknown tracking number.
    /// </summary>
    public async Task<bool> AdvanceAsync(string trackingNumber, TrackingStatus status, DateTimeOffset? occurredAt = null, bool duplicate = false, CancellationToken cancellationToken = default)
    {
        if (!store.Parcels.TryGetValue(trackingNumber, out var parcel))
            return false;
        if (status is >= TrackingStatus.InTransit and not TrackingStatus.Cancelled)
            store.Parcels[trackingNumber] = parcel with { Scanned = true };
        var at = occurredAt ?? time.GetUtcNow();
        var id = "tev_" + store.Next();
        store.Events.Add(new CarrierTrackingEvent(id, trackingNumber, parcel.ParcelId, status, status.ToString(), $"Test: {status}", at));
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            trackingNumber,
            parcelId = parcel.ParcelId,
            status = status.ToString(),
            description = $"Test: {status}",
            occurredAt = at,
        });
        await DeliverAsync(body, cancellationToken);
        if (duplicate)
            await DeliverAsync(body, cancellationToken);
        return true;
    }

    private async Task DeliverAsync(byte[] body, CancellationToken cancellationToken)
    {
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(store.WebhookSecret), body));
        var request = new WebhookRequest(
        [
            new("Content-Type", new Microsoft.Extensions.Primitives.StringValues("application/json")),
            new(SignatureHeader, new Microsoft.Extensions.Primitives.StringValues(signature)),
        ], body);
        await receiver.ReceiveAsync(ShippingWebhookSources.For(TestCarrier.CarrierName), request, cancellationToken);
    }
}
