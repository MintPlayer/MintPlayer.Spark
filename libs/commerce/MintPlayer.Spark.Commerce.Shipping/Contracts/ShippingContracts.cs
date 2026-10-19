using System.Text.Json.Serialization;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Shipping.Contracts;

/// <summary>A staffed pick-up point or a parcel locker (GB5).</summary>
[SparkContract]
[JsonConverter(typeof(JsonStringEnumConverter<SparkServicePointKind>))]
public enum SparkServicePointKind
{
    [JsonStringEnumMemberName("pickupPoint")] PickupPoint,
    [JsonStringEnumMemberName("parcelLocker")] ParcelLocker,
}

/// <summary>A service point's address.</summary>
[SparkContract]
public sealed record SparkServicePointAddress(string Street, string? HouseNumber, string PostalCode, string City, string Country);

/// <summary>One service point or locker, as <c>GET /spark/commerce/shipping/service-points</c> answers it (GB5).</summary>
/// <param name="Id">The carrier's id: what the order and the label carry.</param>
/// <param name="Carrier">The carrier.</param>
/// <param name="Name">The point's name.</param>
/// <param name="Kind">Pick-up point or parcel locker.</param>
/// <param name="Address">Where it is.</param>
/// <param name="Latitude">For a map, when the host enables one.</param>
/// <param name="Longitude">For a map.</param>
/// <param name="DistanceMetres">From the searched address, when the carrier states it.</param>
/// <param name="OpeningHours">Per day (<c>monday</c> → <c>["08:00-12:00"]</c>), as the carrier states them.</param>
[SparkContract]
public sealed record SparkServicePoint(
    string Id,
    string Carrier,
    string Name,
    SparkServicePointKind Kind,
    SparkServicePointAddress Address,
    double? Latitude,
    double? Longitude,
    int? DistanceMetres,
    IReadOnlyDictionary<string, IReadOnlyList<string>> OpeningHours);

/// <summary>Shipped by a carrier, or collected by the buyer.</summary>
[SparkContract]
[JsonConverter(typeof(JsonStringEnumConverter<SparkShippingMethodKind>))]
public enum SparkShippingMethodKind
{
    [JsonStringEnumMemberName("carrier")] Carrier,
    [JsonStringEnumMemberName("pickup")] Pickup,
}

/// <summary>Who pays import duties (GB6).</summary>
[SparkContract]
[JsonConverter(typeof(JsonStringEnumConverter<SparkIncoterm>))]
public enum SparkIncoterm
{
    /// <summary>Delivered At Place: the buyer pays duties on delivery.</summary>
    [JsonStringEnumMemberName("dap")] Dap,
    /// <summary>Delivered Duty Paid: the seller pays them.</summary>
    [JsonStringEnumMemberName("ddp")] Ddp,
}

/// <summary>One offerable shipping rate, as <c>GET /spark/commerce/shipping/rates</c> answers it.</summary>
/// <param name="Method">The method key; the label is the translation key <c>commerceShipping.method.{method}</c>.</param>
/// <param name="Kind">Carrier or pickup.</param>
/// <param name="Carrier">The carrier, for a carrier method.</param>
/// <param name="Price">The price; zero when <paramref name="Free"/>.</param>
/// <param name="Free">The free-shipping threshold applies.</param>
/// <param name="FreeFrom">The subtotal from which this method is free, when it has a threshold.</param>
/// <param name="ServicePoint">The buyer picks a service point or locker (GB5).</param>
/// <param name="Customs">The shipment needs a customs declaration (GB6).</param>
/// <param name="Incoterm">DAP or DDP, shown to the buyer when <paramref name="Customs"/> (GB6).</param>
/// <param name="PromisedFrom">The earliest delivery date (GC11), a local date.</param>
/// <param name="PromisedBy">The latest delivery date (GC11).</param>
/// <param name="PickupLocation">Where to collect, for pickup.</param>
[SparkContract]
public sealed record SparkShippingRate(
    string Method,
    SparkShippingMethodKind Kind,
    string? Carrier,
    Money Price,
    bool Free,
    Money? FreeFrom,
    bool ServicePoint,
    bool Customs,
    SparkIncoterm? Incoterm,
    DateOnly? PromisedFrom,
    DateOnly? PromisedBy,
    string? PickupLocation);
