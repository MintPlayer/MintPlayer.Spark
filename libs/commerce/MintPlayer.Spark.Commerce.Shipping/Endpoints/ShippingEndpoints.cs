using System.Globalization;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Shipping.Contracts;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Commerce.Shipping.Services;
using MintPlayer.Spark.Endpoints;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Shipping.Endpoints;

internal class ShippingGroup : IEndpointGroup
{
    public static string Prefix => "/spark/commerce/shipping";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) { }
}

/// <summary>
/// <c>GET /spark/commerce/shipping/service-points?country=BE&amp;postalCode=1000[&amp;carrier=…&amp;city=…&amp;street=…&amp;houseNumber=…&amp;kind=parcelLocker&amp;limit=10]</c>:
/// the checkout point picker's search (GB5). Anonymous: the buyer picks before paying, often as a guest. Results are
/// cached per query (<see cref="SparkCommerceShippingOptions.ServicePointCacheDuration"/>), so repeated searches do
/// not reach the carrier's API.
/// </summary>
[MemberOf<ShippingGroup>]
internal sealed partial class SearchServicePoints : IGetEndpoint
{
    public static string Path => "/service-points";

    [Inject] private readonly ISparkShipping shipping;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var q = httpContext.Request.Query;
        if (q["country"].ToString() is not { Length: 2 } country || q["postalCode"].ToString() is not { Length: > 0 and <= 16 } postalCode)
            return addOn.Refusal(httpContext);
        ServicePointKind? kind = q["kind"].ToString() switch
        {
            "pickupPoint" => ServicePointKind.PickupPoint,
            "parcelLocker" => ServicePointKind.ParcelLocker,
            _ => null,
        };
        var query = new ServicePointQuery
        {
            Country = country,
            PostalCode = postalCode,
            City = Bounded(q["city"].ToString(), 80),
            Street = Bounded(q["street"].ToString(), 120),
            HouseNumber = Bounded(q["houseNumber"].ToString(), 16),
            Kind = kind,
            Limit = int.TryParse(q["limit"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var limit) ? limit : 10,
        };
        IReadOnlyList<ServicePoint> points;
        try { points = await shipping.SearchServicePointsAsync(Bounded(q["carrier"].ToString(), 32), query, httpContext.RequestAborted); }
        catch (InvalidOperationException) { return addOn.Refusal(httpContext); }
        catch (CarrierException) { return addOn.Envelope(Array.Empty<SparkServicePoint>(), StatusCodes.Status200OK); }
        return addOn.Envelope(points.Select(ShippingContractMapping.ToContract).ToList(), StatusCodes.Status200OK);
    }

    private static string? Bounded(string value, int max) => value is { Length: > 0 } && value.Length <= max ? value : null;
}

/// <summary>
/// <c>GET /spark/commerce/shipping/rates?country=BE&amp;postalCode=1000&amp;subtotal=42.50[&amp;weight=1200]</c>: the methods a
/// destination is offered, with free-shipping thresholds, the customs choice (GB6) and delivery promises (GC11).
/// Anonymous: the storefront shows "free shipping from" before checkout. Checkout's own shipping step calls
/// <see cref="ISparkShipping.GetRatesAsync"/> with the basket's dangerous goods (GC9).
/// </summary>
[MemberOf<ShippingGroup>]
internal sealed partial class GetShippingRates : IGetEndpoint
{
    public static string Path => "/rates";

    [Inject] private readonly ISparkShipping shipping;
    [Inject] private readonly IOptionsMonitor<SparkCommerceShippingOptions> options;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var q = httpContext.Request.Query;
        if (q["country"].ToString() is not { Length: 2 } country || !ShippingAddressRules.IsCountryCode(country))
            return addOn.Refusal(httpContext);
        var currency = options.CurrentValue.Currency;
        var subtotal = decimal.TryParse(q["subtotal"].ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) && amount >= 0 && amount < 100_000_000m
            ? amount : 0m;
        var weight = int.TryParse(q["weight"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var grams) ? grams : 0;
        var rates = await shipping.GetRatesAsync(new ShippingRateRequest
        {
            ShipTo = new ShippingAddress { Country = country, PostalCode = q["postalCode"].ToString() is { Length: <= 16 } postal ? postal : string.Empty },
            Subtotal = new Money(Math.Round(subtotal, Money.MinorUnitDigits(currency)), currency),
            WeightGrams = weight,
        }, httpContext.RequestAborted);
        return addOn.Envelope(rates.Select(ShippingContractMapping.ToContract).ToList(), StatusCodes.Status200OK);
    }
}

/// <summary>
/// <c>GET /spark/commerce/shipping/label?id=…&amp;parcel=1</c>: a parcel's label (PDF or ZPL) for the pack station or the
/// shop's staff. Needs <c>Read/Shipment</c> and the row filters (<see cref="IShipmentRowFilter"/>).
/// </summary>
[MemberOf<ShippingGroup>]
internal sealed partial class DownloadShippingLabel : IGetEndpoint
{
    public static string Path => "/label";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization();

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ShipmentAccess access;
    [Inject] private readonly ISparkShipping shipping;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (httpContext.Request.Query["id"].ToString() is not { Length: > 0 } id
            || !int.TryParse(httpContext.Request.Query["parcel"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parcel))
            return addOn.Refusal(httpContext);
        if (!await ShippingContractMapping.MayReadAsync(store, access, id, httpContext.RequestAborted))
            return addOn.Refusal(httpContext);
        if (await shipping.GetLabelAsync(id, parcel, httpContext.RequestAborted) is not { } label)
            return addOn.Refusal(httpContext);
        return Results.File(label.Content, label.MediaType, ShippingContractMapping.FileName(id, $"label-{parcel}", label.MediaType));
    }
}

/// <summary><c>GET /spark/commerce/shipping/customs-document?id=…</c>: the CN22/CN23 or commercial invoice the carrier returned (GB6).</summary>
[MemberOf<ShippingGroup>]
internal sealed partial class DownloadCustomsDocument : IGetEndpoint
{
    public static string Path => "/customs-document";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization();

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ShipmentAccess access;
    [Inject] private readonly ISparkShipping shipping;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (httpContext.Request.Query["id"].ToString() is not { Length: > 0 } id
            || !await ShippingContractMapping.MayReadAsync(store, access, id, httpContext.RequestAborted))
            return addOn.Refusal(httpContext);
        if (await shipping.GetCustomsDocumentAsync(id, httpContext.RequestAborted) is not { } document)
            return addOn.Refusal(httpContext);
        return Results.File(document.Content, document.MediaType, ShippingContractMapping.FileName(id, "customs", document.MediaType));
    }
}

/// <summary>Maps the service's records onto the TypeScript contracts.</summary>
internal static class ShippingContractMapping
{
    public static SparkServicePoint ToContract(ServicePoint point) => new(
        point.Id,
        point.Carrier,
        point.Name,
        point.Kind == ServicePointKind.ParcelLocker ? SparkServicePointKind.ParcelLocker : SparkServicePointKind.PickupPoint,
        new SparkServicePointAddress(point.Address.Street, point.Address.HouseNumber, point.Address.PostalCode, point.Address.City, point.Address.Country),
        point.Latitude,
        point.Longitude,
        point.DistanceMetres,
        point.OpeningHours);

    public static SparkShippingRate ToContract(ShippingRate rate) => new(
        rate.Method,
        rate.Kind == ShippingMethodKind.Pickup ? SparkShippingMethodKind.Pickup : SparkShippingMethodKind.Carrier,
        rate.Carrier,
        rate.Price,
        rate.Free,
        rate.FreeFrom,
        rate.ServicePoint,
        rate.Customs != CustomsReason.None,
        rate.Incoterm switch { Incoterm.Ddp => SparkIncoterm.Ddp, Incoterm.Dap => SparkIncoterm.Dap, _ => null },
        rate.Promise?.From,
        rate.Promise?.By,
        rate.PickupLocation);

    public static async Task<bool> MayReadAsync(IDocumentStore store, ShipmentAccess access, string id, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var shipment = await session.LoadAsync<Shipment>(id, cancellationToken);
        return shipment is not null && await access.CanReadAsync(shipment, cancellationToken);
    }

    public static string FileName(string id, string stem, string mediaType)
        => "shipment-" + new string(id.Where(char.IsLetterOrDigit).ToArray()) + "-" + stem + mediaType switch
        {
            CarrierDocument.Zpl => ".zpl",
            CarrierDocument.Png => ".png",
            _ => ".pdf",
        };
}
