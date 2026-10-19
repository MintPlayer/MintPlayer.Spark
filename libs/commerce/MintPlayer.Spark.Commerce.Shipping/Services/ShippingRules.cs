using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Shipping.Models;

namespace MintPlayer.Spark.Commerce.Shipping;

/// <summary>Dangerous-goods rules (GC9): which services may carry a parcel, and what its label must say.</summary>
public static class DangerousGoodsRules
{
    /// <summary>The UN number a lithium class implies; null for the classes that name none.</summary>
    public static string? DefaultUnNumber(DangerousGoodsClass @class) => @class switch
    {
        DangerousGoodsClass.LithiumIonInEquipment or DangerousGoodsClass.LithiumIonPackedWithEquipment => "UN3481",
        DangerousGoodsClass.LithiumIonBatteries => "UN3480",
        DangerousGoodsClass.LithiumMetalInEquipment or DangerousGoodsClass.LithiumMetalPackedWithEquipment => "UN3091",
        DangerousGoodsClass.LithiumMetalBatteries => "UN3090",
        _ => null,
    };

    /// <summary>Whether <paramref name="service"/> accepts every class in <paramref name="items"/>. No items: always.</summary>
    public static bool Accepts(CarrierService service, IEnumerable<DangerousGoodsItem> items)
        => items.All(i => service.AcceptedDangerousGoods.Contains(i.Class));

    /// <summary>
    /// The marks the label must carry, one per distinct class, for adapters whose API takes them
    /// (<see cref="CarrierCapabilities.DangerousGoodsMarks"/>): the lithium battery mark with its UN number
    /// (ADR 5.2.1.9), the limited-quantity mark (ADR 3.4.7) and the excepted-quantity mark (ADR 3.5.4). Plain text
    /// keys; an adapter maps them onto the carrier's own fields.
    /// </summary>
    public static IReadOnlyList<string> Marks(IEnumerable<DangerousGoodsItem> items)
        => items
            .Select(i => i.Class switch
            {
                DangerousGoodsClass.LimitedQuantity => "LQ",
                DangerousGoodsClass.ExceptedQuantity => "EQ",
                DangerousGoodsClass.Other => i.UnNumber ?? "DG",
                _ => "LITHIUM " + (i.UnNumber ?? DefaultUnNumber(i.Class)),
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>The items with their UN numbers filled from their class where the caller left them out.</summary>
    public static List<DangerousGoodsItem> Normalize(IEnumerable<DangerousGoodsItem>? items)
        => (items ?? [])
            .Select(i => new DangerousGoodsItem { Class = i.Class, UnNumber = i.UnNumber is { Length: > 0 } un ? un.Trim().ToUpperInvariant() : DefaultUnNumber(i.Class) })
            .DistinctBy(i => (i.Class, i.UnNumber))
            .ToList();
}

/// <summary>Why a shipment needs customs (GB6).</summary>
public enum CustomsReason
{
    /// <summary>Inside the EU customs and VAT territory: no declaration.</summary>
    None,
    /// <summary>The destination (or origin) is outside the EU customs territory.</summary>
    OutsideCustomsTerritory,
    /// <summary>Inside the customs territory but outside the VAT territory (Canary Islands, Åland, Mount Athos).</summary>
    OutsideVatTerritory,
}

/// <summary>Customs rules (GB6).</summary>
public static partial class CustomsRules
{
    /// <summary>
    /// Whether a shipment from <paramref name="from"/> to <paramref name="to"/> needs a customs declaration: when either
    /// end is outside the EU customs territory, or outside the VAT territory, and the two ends are not in the same
    /// such area (a parcel inside Switzerland needs none).
    /// </summary>
    public static CustomsReason Requirement(ShippingAddress from, ShippingAddress to, CustomsOptions options)
    {
        var a = Area(from, options);
        var b = Area(to, options);
        if (string.Equals(a.Key, b.Key, StringComparison.Ordinal))
            return CustomsReason.None;
        return a.Reason == CustomsReason.OutsideCustomsTerritory || b.Reason == CustomsReason.OutsideCustomsTerritory
            ? CustomsReason.OutsideCustomsTerritory
            : a.Reason == CustomsReason.None ? b.Reason : a.Reason;
    }

    /// <summary>
    /// The document for a service style and a declared value: a commercial invoice for express services, CN23 for
    /// postal parcels, and for letter post CN22 up to <see cref="CustomsOptions.Cn22MaxValue"/> and CN23 above it
    /// (UPU Convention regulations: CN22 up to 300 SDR). Confirm per carrier.
    /// </summary>
    public static CustomsDocumentKind DocumentFor(CustomsDocumentStyle style, Money declaredValue, CustomsOptions options) => style switch
    {
        CustomsDocumentStyle.CommercialInvoice => CustomsDocumentKind.CommercialInvoice,
        CustomsDocumentStyle.LetterPost when declaredValue.Amount <= options.Cn22MaxValue => CustomsDocumentKind.Cn22,
        _ => CustomsDocumentKind.Cn23,
    };

    /// <summary>The problems with a declaration's items, empty when it is complete (every line: HS code, origin, value, weight).</summary>
    public static IReadOnlyList<string> Validate(CustomsDeclaration declaration)
    {
        var problems = new List<string>();
        if (declaration.Items.Count == 0)
            problems.Add("A customs declaration needs at least one item.");
        foreach (var (item, index) in declaration.Items.Select((item, index) => (item, index + 1)))
        {
            if (!HsCodePattern().IsMatch(item.HsCode ?? string.Empty))
                problems.Add($"Item {index}: the HS code must be 6, 8 or 10 digits.");
            if (!ShippingAddressRules.IsCountryCode(item.OriginCountry))
                problems.Add($"Item {index}: the country of origin must be an ISO 3166-1 alpha-2 code.");
            if (item.Quantity <= 0)
                problems.Add($"Item {index}: the quantity must be positive.");
            if (item.WeightGrams <= 0)
                problems.Add($"Item {index}: the weight must be positive.");
            if (string.IsNullOrWhiteSpace(item.Description))
                problems.Add($"Item {index}: a description is required.");
        }
        if (declaration.Items.Select(i => i.Value.Currency).Distinct(StringComparer.Ordinal).Count() > 1)
            problems.Add("Every item of a customs declaration is declared in one currency.");
        return problems;
    }

    private readonly record struct AreaKey(CustomsReason Reason, string Key);

    private static AreaKey Area(ShippingAddress address, CustomsOptions options)
    {
        var country = (address.Country ?? string.Empty).Trim().ToUpperInvariant();
        var postal = (address.PostalCode ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal);
        foreach (var exception in options.TerritoryExceptions)
        {
            if (string.Equals(exception.Country, country, StringComparison.OrdinalIgnoreCase)
                && exception.PostalCodePrefix is { Length: > 0 } prefix
                && postal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var fiscal = exception.Note?.Contains("VAT territory", StringComparison.OrdinalIgnoreCase) == true;
                return new(fiscal ? CustomsReason.OutsideVatTerritory : CustomsReason.OutsideCustomsTerritory, country + ":" + prefix);
            }
        }
        return options.EffectiveCustomsTerritory.Contains(country, StringComparer.OrdinalIgnoreCase)
            ? new(CustomsReason.None, "EU")
            : new(CustomsReason.OutsideCustomsTerritory, country);
    }

    [GeneratedRegex("^([0-9]{6}|[0-9]{8}|[0-9]{10})$", RegexOptions.CultureInvariant)]
    private static partial Regex HsCodePattern();
}

/// <summary>The shape checks of a <see cref="ShippingAddress"/>.</summary>
public static partial class ShippingAddressRules
{
    /// <summary>An ISO 3166-1 alpha-2 shape (two ASCII letters).</summary>
    public static bool IsCountryCode(string? value) => value is { Length: 2 } && CountryPattern().IsMatch(value);

    /// <summary>The problems with an address, empty when carriers can take it.</summary>
    public static IReadOnlyList<string> Validate(ShippingAddress? address, string role)
    {
        if (address is null)
            return [$"The {role} address is missing."];
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(address.Name) && string.IsNullOrWhiteSpace(address.Company))
            problems.Add($"The {role} address needs a name or a company.");
        if (string.IsNullOrWhiteSpace(address.Street))
            problems.Add($"The {role} address needs a street.");
        if (string.IsNullOrWhiteSpace(address.City))
            problems.Add($"The {role} address needs a city.");
        if (!IsCountryCode(address.Country))
            problems.Add($"The {role} address needs an ISO 3166-1 alpha-2 country.");
        if (address.PostalCode is { Length: > 16 })
            problems.Add($"The {role} address has a postal code longer than 16 characters.");
        return problems;
    }

    /// <summary>A copy with the country upper-cased and the fields trimmed.</summary>
    public static ShippingAddress Normalize(ShippingAddress address) => new()
    {
        Name = address.Name?.Trim() ?? string.Empty,
        Company = address.Company?.Trim() is { Length: > 0 } company ? company : null,
        Street = address.Street?.Trim() ?? string.Empty,
        HouseNumber = address.HouseNumber?.Trim() is { Length: > 0 } number ? number : null,
        Addition = address.Addition?.Trim() is { Length: > 0 } addition ? addition : null,
        PostalCode = address.PostalCode?.Trim() ?? string.Empty,
        City = address.City?.Trim() ?? string.Empty,
        Country = (address.Country ?? string.Empty).Trim().ToUpperInvariant(),
        Subdivision = address.Subdivision?.Trim() is { Length: > 0 } subdivision ? subdivision : null,
        Email = address.Email?.Trim() is { Length: > 0 } email ? email : null,
        Phone = address.Phone?.Trim() is { Length: > 0 } phone ? phone : null,
    };

    [GeneratedRegex("^[A-Za-z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex CountryPattern();
}

/// <summary>Zone lookup (§4.9). Zones are keyed on the delivery country, which the Geo-blocking Regulation allows (GA11).</summary>
public static class ShippingZones
{
    /// <summary>The zone that lists <paramref name="country"/>, else the zone that lists <c>*</c>, else null.</summary>
    public static string? Resolve(IReadOnlyDictionary<string, ShippingZoneOptions> zones, string? country)
    {
        var code = (country ?? string.Empty).Trim();
        string? wildcard = null;
        foreach (var (name, zone) in zones)
        {
            if (zone.Countries.Any(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase)))
                return name;
            if (wildcard is null && zone.Countries.Contains("*"))
                wildcard = name;
        }
        return wildcard;
    }
}
