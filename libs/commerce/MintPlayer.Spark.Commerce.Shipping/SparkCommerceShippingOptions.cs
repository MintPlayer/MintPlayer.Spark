using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Commerce.Shipping.Models;

namespace MintPlayer.Spark.Commerce.Shipping;

/// <summary>
/// <c>Spark:Commerce:Shipping</c> (commerce PRD §4.9). Methods and zones come from configuration (K2); carrier
/// credentials live under each adapter's own section (<c>Spark:Commerce:Shipping:Sendcloud</c>, …) and come from
/// configuration or user-secrets only.
/// </summary>
/// <example>
/// <code>
/// "Spark": { "Commerce": { "Shipping": {
///   "Carrier": "sendcloud",
///   "Sender": { "Name": "Shop", "Street": "Rue de la Loi", "HouseNumber": "16", "PostalCode": "1000", "City": "Brussels", "Country": "BE" },
///   "Zones": {
///     "benelux": { "Countries": [ "BE", "NL", "LU" ] },
///     "uk":      { "Countries": [ "GB" ], "Incoterm": "Ddp" },
///     "world":   { "Countries": [ "*" ] }
///   },
///   "Methods": {
///     "standard": { "Kind": "Carrier", "Carrier": "sendcloud", "Service": "8", "Rates": { "benelux": 4.95, "world": 19.95 }, "FreeFrom": { "benelux": 50 }, "TransitDays": { "Min": 1, "Max": 2 } },
///     "locker":   { "Kind": "Carrier", "Carrier": "bpost", "Service": "bpack 24/7", "Rates": { "benelux": 3.95 } },
///     "pickup":   { "Kind": "Pickup", "Rates": { "benelux": 0 }, "PickupLocation": "Shop, Rue de la Loi 16, Brussels" }
///   },
///   "Dispatch": { "TimeZone": "Europe/Brussels", "CutOff": "15:00", "HolidayCountry": "BE" }
/// } } }
/// </code>
/// </example>
public sealed class SparkCommerceShippingOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Shipping";

    /// <summary>The carrier used when a call names none. Required when more than one adapter is registered.</summary>
    public string? Carrier { get; set; }

    /// <summary>The currency of every configured rate and threshold (ISO 4217).</summary>
    public string Currency { get; set; } = "EUR";

    /// <summary>The ship-from address of outbound shipments; a warehouse site passes its own per call.</summary>
    public ShippingAddress? Sender { get; set; }

    /// <summary>Where return labels (GB5) send parcels; <see cref="Sender"/> when unset.</summary>
    public ShippingAddress? ReturnAddress { get; set; }

    /// <summary>Destination zones by name. A zone lists ISO 3166-1 alpha-2 countries; <c>*</c> is every other country.</summary>
    public Dictionary<string, ShippingZoneOptions> Zones { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The shipping methods offered at checkout, by key.</summary>
    public Dictionary<string, ShippingMethodOptions> Methods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cut-offs and working days (GC11).</summary>
    public DispatchOptions Dispatch { get; set; } = new();

    /// <summary>Customs (GB6).</summary>
    public CustomsOptions Customs { get; set; } = new();

    /// <summary>The label format asked of carriers; an adapter falls back to PDF when the carrier cannot.</summary>
    public LabelFormat LabelFormat { get; set; } = LabelFormat.Pdf;

    /// <summary>
    /// How often parcels of carriers without webhooks (bpost, PostNL) are polled for tracking (NCrontab, UTC). Read at
    /// registration.
    /// </summary>
    public string TrackingPollSchedule { get; set; } = "*/30 * * * *";

    /// <summary>Parcels not final after this long stop being polled.</summary>
    public TimeSpan TrackingPollMaxAge { get; set; } = TimeSpan.FromDays(45);

    /// <summary>How long a service-point search is cached (anonymous endpoint; carriers rate-limit their APIs).</summary>
    public TimeSpan ServicePointCacheDuration { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>One destination zone.</summary>
public sealed class ShippingZoneOptions
{
    /// <summary>ISO 3166-1 alpha-2 codes, upper case; <c>*</c> for every country no other zone lists.</summary>
    public List<string> Countries { get; set; } = [];

    /// <summary>
    /// DAP (buyer pays duties) or DDP (seller pays) for shipments that need customs (GB6). Checkout shows the
    /// choice to the buyer.
    /// </summary>
    public Incoterm Incoterm { get; set; } = Incoterm.Dap;
}

/// <summary>The kind of a configured method.</summary>
public enum ShippingMethodKind
{
    /// <summary>A flat rate per zone, shipped with a carrier service.</summary>
    Carrier,
    /// <summary>The buyer collects at the shop: no carrier, no label.</summary>
    Pickup,
}

/// <summary>One configured shipping method.</summary>
public sealed class ShippingMethodOptions
{
    public ShippingMethodKind Kind { get; set; } = ShippingMethodKind.Carrier;

    /// <summary>The carrier; <see cref="SparkCommerceShippingOptions.Carrier"/> when unset.</summary>
    public string? Carrier { get; set; }

    /// <summary>The carrier service's code (<see cref="CarrierService.Code"/>).</summary>
    public string? Service { get; set; }

    /// <summary>The flat rate per zone. A zone without a rate is not served by this method.</summary>
    public Dictionary<string, decimal> Rates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Free shipping from this goods subtotal (inclusive), per zone.</summary>
    public Dictionary<string, decimal> FreeFrom { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The heaviest shipment this method takes, in grams; 0 for the service's own limit.</summary>
    public int MaxWeightGrams { get; set; }

    /// <summary>Working days in transit, for the delivery promise (GC11). Unset: no promise.</summary>
    public TransitDaysOptions? TransitDays { get; set; }

    /// <summary>For <see cref="ShippingMethodKind.Pickup"/>: where to collect, shown to the buyer.</summary>
    public string? PickupLocation { get; set; }

    /// <summary>Sort order at checkout.</summary>
    public int Order { get; set; }
}

/// <summary>Minimum and maximum working days in transit.</summary>
public sealed class TransitDaysOptions
{
    public int Min { get; set; } = 1;

    public int Max { get; set; } = 1;
}

/// <summary>Dispatch cut-offs and working days (GC11).</summary>
public sealed class DispatchOptions
{
    /// <summary>The IANA zone the cut-off is stated in (the warehouse or shop's own).</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>Orders paid before this wall-clock time leave the same working day.</summary>
    public TimeOnly CutOff { get; set; } = new(15, 0);

    /// <summary>
    /// The days parcels leave; Monday to Friday when unset. (Null rather than a filled default: the configuration
    /// binder appends to a list, so a default could never be narrowed.)
    /// </summary>
    public List<DayOfWeek>? WorkingDays { get; set; }

    /// <summary>The effective working days.</summary>
    public IReadOnlyList<DayOfWeek> EffectiveWorkingDays => WorkingDays is { Count: > 0 } days
        ? days
        : [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    /// <summary>The country whose public holidays stop dispatch (ISO 3166-1 alpha-2); none when unset.</summary>
    public string? HolidayCountry { get; set; }

    /// <summary>The ISO 3166-2 subdivision for regional holidays, when one applies.</summary>
    public string? HolidaySubdivision { get; set; }

    /// <summary>Also skip the destination country's public holidays in the transit days of a delivery promise.</summary>
    public bool SkipDestinationHolidays { get; set; } = true;
}

/// <summary>Customs (GB6).</summary>
public sealed class CustomsOptions
{
    /// <summary>
    /// The EU customs territory, by country (Regulation (EU) No 952/2013, Art. 4). The default is the 27 member
    /// states when unset (null rather than a filled default, which the binder could only append to);
    /// <see cref="TerritoryExceptions"/> refines it by postal code.
    /// </summary>
    public List<string>? CustomsTerritory { get; set; }

    /// <summary>The effective customs territory.</summary>
    public IReadOnlyList<string> EffectiveCustomsTerritory => CustomsTerritory is { Count: > 0 } list ? list : EuMemberStates;

    /// <summary>The 27 member states (ISO 3166-1 alpha-2; Greece is <c>GR</c>, not the EU's own <c>EL</c>).</summary>
    public static IReadOnlyList<string> EuMemberStates { get; } =
    [
        "AT", "BE", "BG", "CY", "CZ", "DE", "DK", "EE", "ES", "FI", "FR", "GR", "HR", "HU", "IE", "IT", "LT", "LU",
        "LV", "MT", "NL", "PL", "PT", "RO", "SE", "SI", "SK",
    ];

    /// <summary>
    /// Areas of a member state that are outside the customs territory (Art. 4: Heligoland, Büsingen, Ceuta, Melilla,
    /// Livigno, Campione d'Italia) or inside it but outside the VAT territory, where postal items still need a
    /// declaration (the Canary Islands, Åland, Mount Athos). Matched on the postal-code prefix. The shipped list is a
    /// starting point: confirm per country before relying on it (D37). Configured entries are appended to it.
    /// </summary>
    public List<CustomsTerritoryException> TerritoryExceptions { get; set; } =
    [
        new() { Country = "DE", PostalCodePrefix = "27498", Note = "Heligoland: outside the customs territory (UCC Art. 4)" },
        new() { Country = "DE", PostalCodePrefix = "78266", Note = "Büsingen am Hochrhein: outside the customs territory (UCC Art. 4)" },
        new() { Country = "ES", PostalCodePrefix = "51", Note = "Ceuta: outside the customs territory (UCC Art. 4)" },
        new() { Country = "ES", PostalCodePrefix = "52", Note = "Melilla: outside the customs territory (UCC Art. 4)" },
        new() { Country = "ES", PostalCodePrefix = "35", Note = "Las Palmas (Canary Islands): outside the VAT territory, declaration for fiscal purposes" },
        new() { Country = "ES", PostalCodePrefix = "38", Note = "Santa Cruz de Tenerife (Canary Islands): outside the VAT territory, declaration for fiscal purposes" },
        new() { Country = "IT", PostalCodePrefix = "23030", Note = "Livigno: outside the customs territory (UCC Art. 4); the prefix also covers neighbouring towns, confirm" },
        new() { Country = "IT", PostalCodePrefix = "22061", Note = "Campione d'Italia: outside the customs territory (UCC Art. 4)" },
        new() { Country = "FI", PostalCodePrefix = "22", Note = "Åland: outside the VAT territory, declaration for fiscal purposes" },
        new() { Country = "GR", PostalCodePrefix = "63086", Note = "Mount Athos: outside the VAT territory, declaration for fiscal purposes" },
        new() { Country = "GR", PostalCodePrefix = "63087", Note = "Mount Athos: outside the VAT territory, declaration for fiscal purposes" },
    ];

    /// <summary>
    /// The highest value a letter-post item may declare on a CN22; above it a CN23 is used. The UPU sets the limit at
    /// 300 SDR; the default, 300 in <see cref="SparkCommerceShippingOptions.Currency"/>, stays under it for EUR
    /// (1 SDR is above 1 EUR), so it never picks a CN22 the UPU would refuse. Confirm per carrier.
    /// </summary>
    public decimal Cn22MaxValue { get; set; } = 300m;

    /// <summary>The content type declared when the caller states none.</summary>
    public CustomsContentType DefaultContentType { get; set; } = CustomsContentType.CommercialGoods;
}

/// <summary>A postal-code area treated differently from the rest of its country (GB6).</summary>
public sealed class CustomsTerritoryException
{
    public string Country { get; set; } = string.Empty;

    public string PostalCodePrefix { get; set; } = string.Empty;

    /// <summary>Why; for people reading the configuration.</summary>
    public string? Note { get; set; }
}
