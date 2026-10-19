using System.Globalization;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Gs1;

namespace MintPlayer.Spark.Commerce.Shop.Catalog;

/// <summary>
/// The catalogue's pure rules: identifiers (SKU, GTIN, HS code, country), what must hold before a product is published,
/// and the computed values (unit price, facet terms, search texts). No I/O, no clock.
/// </summary>
public static partial class ProductRules
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_./-]{0,63}$")]
    private static partial Regex SkuPattern();

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex KeyPattern();

    /// <summary>
    /// The member states of the EU (ISO 3166-1 alpha-2): a manufacturer established elsewhere needs an EU responsible
    /// person (GPSR Art. 16 and 19(b); Reg. (EU) 2019/1020 Art. 4). ⚠️ Northern Ireland (<c>XI</c>) follows EU product
    /// rules under the Windsor Framework; the host may list it as well (confirm, D37).
    /// </summary>
    public static readonly IReadOnlySet<string> EuMemberStates = new HashSet<string>(StringComparer.Ordinal)
    {
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU",
        "MT", "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE",
    };

    public static bool IsSku(string? sku) => sku is not null && SkuPattern().IsMatch(sku);

    /// <summary>A facet or option key: lower-case letters, digits and dashes, at most 40.</summary>
    public static bool IsKey(string? key) => key is { Length: > 0 and <= 40 } && KeyPattern().IsMatch(key);

    /// <summary>A GTIN-8, -12, -13 or -14 (digits only) whose last digit is its GS1 check digit (GS1 General Specifications §7.9).</summary>
    public static bool IsGtin(string? gtin)
        => gtin is { Length: 8 or 12 or 13 or 14 } && gtin.All(char.IsAsciiDigit) && Gs1CheckDigit.IsValid(gtin);

    /// <summary>An HS code: the six-digit Harmonized System heading, or the eight-digit CN or ten-digit TARIC extension of it.</summary>
    public static bool IsHsCode(string? code) => code is { Length: 6 or 8 or 10 } && code.All(char.IsAsciiDigit);

    /// <summary>An ISO 3166-1 alpha-2 shape (two upper-case letters).</summary>
    public static bool IsCountry(string? code) => code is { Length: 2 } && code.All(char.IsAsciiLetterUpper);

    /// <summary>The variant's effective unit price: its own, else the product's.</summary>
    public static Money? PriceOf(ShopProduct product, ShopVariant variant) => variant.Price ?? product.Price;

    /// <summary>The variant's effective net quantity (GA3).</summary>
    public static decimal? NetQuantityOf(ShopProduct product, ShopVariant? variant) => variant?.NetQuantity ?? product.NetQuantity;

    /// <summary>
    /// GA3 (Price Indication Directive 98/6/EC Art. 3(1) and 2(b)): the price for <paramref name="reference"/> units of
    /// measure (1 kg, 1 l, …), computed from the final price of one item and its net quantity, never typed. Rounded once
    /// to the currency's minor unit, half away from zero (<see cref="Money.RoundDiv(Int128, Int128)"/>). Null when the
    /// product has no unit of measure or no positive net quantity.
    /// </summary>
    public static Money? UnitPrice(Money finalUnitPrice, EUnitOfMeasure unit, decimal? netQuantity, decimal? reference = null)
    {
        if (unit == EUnitOfMeasure.None || netQuantity is not { } net || net <= 0)
            return null;
        var per = reference is { } r && r > 0 ? r : 1m;
        const decimal scale = 1_000_000m;
        var numerator = (Int128)finalUnitPrice.ToMinorUnits() * (Int128)decimal.Round(per * scale);
        var denominator = (Int128)decimal.Round(net * scale);
        if (denominator <= 0)
            return null;
        return Money.FromMinorUnits((long)Money.RoundDiv(numerator, denominator), finalUnitPrice.Currency);
    }

    /// <summary>The symbol a unit price is shown per: <c>kg</c>, <c>l</c>, <c>m</c>, <c>m²</c>, <c>m³</c>, <c>item</c>.</summary>
    public static string UnitSymbol(EUnitOfMeasure unit) => unit switch
    {
        EUnitOfMeasure.Kilogram => "kg",
        EUnitOfMeasure.Litre => "l",
        EUnitOfMeasure.Metre => "m",
        EUnitOfMeasure.SquareMetre => "m²",
        EUnitOfMeasure.CubicMetre => "m³",
        EUnitOfMeasure.Piece => "item",
        _ => "",
    };

    /// <summary>
    /// GA7 (Reg. (EU) 2023/988 Art. 19): what is missing from a goods product's safety information, or nothing. A
    /// product that is not goods needs none.
    /// </summary>
    public static List<string> SafetyProblems(ShopProduct product, IReadOnlyCollection<ShopVariant> variants)
    {
        var problems = new List<string>();
        if (product.Kind != EShopProductKind.Goods)
            return problems;
        if (string.IsNullOrWhiteSpace(product.ManufacturerName))
            problems.Add("The manufacturer's name is missing (GPSR Art. 19(a)).");
        if (string.IsNullOrWhiteSpace(product.ManufacturerAddress))
            problems.Add("The manufacturer's postal address is missing (GPSR Art. 19(a)).");
        if (string.IsNullOrWhiteSpace(product.ManufacturerContact))
            problems.Add("The manufacturer's electronic address is missing (GPSR Art. 19(a)).");
        if (!IsCountry(product.ManufacturerCountry))
            problems.Add("The manufacturer's country is missing: it decides whether an EU responsible person is required (GPSR Art. 19(b)).");
        else if (!EuMemberStates.Contains(product.ManufacturerCountry!)
            && (string.IsNullOrWhiteSpace(product.ResponsiblePersonName) || string.IsNullOrWhiteSpace(product.ResponsiblePersonAddress)
                || string.IsNullOrWhiteSpace(product.ResponsiblePersonContact)))
            problems.Add("The manufacturer is established outside the EU: the EU responsible person's name, postal and electronic address are required (GPSR Art. 19(b)).");
        if (string.IsNullOrWhiteSpace(product.SafetyIdentifier) && !variants.Any(v => !string.IsNullOrEmpty(v.Gtin)))
            problems.Add("No product identifier: give a type, batch, serial number or model, or a GTIN on a variant (GPSR Art. 19(c)).");
        if (string.IsNullOrWhiteSpace(product.SafetyPictureUrl) && product.Media.Count == 0)
            problems.Add("No picture of the product (GPSR Art. 19(c)).");
        return problems;
    }

    /// <summary>
    /// What prevents <paramref name="product"/> from selling (English, for the validation message), or nothing. Pure: the
    /// seller gate and the legal documents are checked by the publisher.
    /// </summary>
    public static List<string> Problems(ShopProduct product, IReadOnlyList<ShopVariant> variants, SparkCommerceOptions commerce)
    {
        var problems = new List<string>();
        if (product.Name.IsEmpty)
            problems.Add("The product has no name.");
        if (!ShopSlugs.IsSlug(product.Slug))
            problems.Add($"The slug '{product.Slug}' is not lower-case letters, digits and dashes.");
        if (!commerce.Channels.ContainsKey(product.Channel))
            problems.Add($"The channel '{product.Channel}' is not configured in Spark:Commerce:Channels.");

        var active = variants.Where(v => v.IsActive).ToList();
        if (active.Count == 0)
            problems.Add("The product has no active variant.");
        foreach (var variant in variants)
            problems.AddRange(VariantProblems(product, variant));
        foreach (var duplicate in variants.GroupBy(v => v.Sku, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"The SKU '{duplicate.Key}' is used by several variants.");
        var currencies = active.Select(v => PriceOf(product, v)?.Currency).Where(c => c is not null).Distinct(StringComparer.Ordinal).ToList();
        if (currencies.Count > 1)
            problems.Add("Every variant of a product is priced in one currency.");

        if (product.UnitOfMeasure != EUnitOfMeasure.None && active.Any(v => NetQuantityOf(product, v) is not > 0))
            problems.Add("A product sold by quantity needs a net quantity on the product or every variant (unit price, PID Art. 3).");
        if (product.Media.Any(m => string.IsNullOrWhiteSpace(m.Url) || m.Alt.IsEmpty))
            problems.Add("Every image needs a URL and an alternative text (WCAG 2.1 SC 1.1.1).");
        var variantIds = variants.Select(v => v.Id).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        if (product.Media.Any(m => m.VariantIds.Any(id => !variantIds.Contains(id))))
            problems.Add("An image names a variant of another product.");
        if (product.Attributes.Any(a => !IsKey(a.Name) || string.IsNullOrWhiteSpace(a.Value)))
            problems.Add("An attribute needs a key (lower-case letters, digits and dashes) and a value.");
        if (product.HsCode is { Length: > 0 } hs && !IsHsCode(hs))
            problems.Add($"'{hs}' is not an HS code (6, 8 or 10 digits).");
        if (product.OriginCountry is { Length: > 0 } origin && !IsCountry(origin))
            problems.Add($"'{origin}' is not a country code.");
        if (product.MinimumAge is < 0 or > 120)
            problems.Add("The minimum age lies between 0 and 120.");
        if (product.DurabilityGuaranteeMonths is <= 0)
            problems.Add("A durability guarantee lasts at least one month.");
        problems.AddRange(SafetyProblems(product, variants));
        return problems;
    }

    /// <summary>One variant's own problems (also checked on every variant save).</summary>
    public static List<string> VariantProblems(ShopProduct product, ShopVariant variant)
    {
        var problems = new List<string>();
        var name = string.IsNullOrEmpty(variant.Sku) ? variant.Id ?? "a variant" : variant.Sku;
        if (!IsSku(variant.Sku))
            problems.Add($"Variant {name}: the SKU must be letters, digits, '-', '_', '.' or '/' (at most 64).");
        if (variant.Gtin is { Length: > 0 } gtin && !IsGtin(gtin))
            problems.Add($"Variant {name}: '{gtin}' is not a GTIN-8, -12, -13 or -14 with a valid check digit.");
        if (variant.IsActive && PriceOf(product, variant) is not { } price)
            problems.Add($"Variant {name} has no price.");
        else if (PriceOf(product, variant) is { Amount: < 0 })
            problems.Add($"Variant {name} has a negative price.");
        if (variant.TrackInventory && variant.Capacity < 0)
            problems.Add($"Variant {name} has a negative capacity.");
        if (variant.WeightGrams is < 0)
            problems.Add($"Variant {name} has a negative weight.");
        if (variant.NetQuantity is <= 0)
            problems.Add($"Variant {name}: the net quantity must be positive.");
        if (variant.HsCode is { Length: > 0 } hs && !IsHsCode(hs))
            problems.Add($"Variant {name}: '{hs}' is not an HS code (6, 8 or 10 digits).");
        if (variant.OriginCountry is { Length: > 0 } origin && !IsCountry(origin))
            problems.Add($"Variant {name}: '{origin}' is not a country code.");
        if (variant.Options.Any(o => !IsKey(o.Name) || string.IsNullOrWhiteSpace(o.Value)))
            problems.Add($"Variant {name}: an option needs a key (lower-case letters, digits and dashes) and a value.");
        if (variant.Options.GroupBy(o => o.Name, StringComparer.Ordinal).Any(g => g.Count() > 1))
            problems.Add($"Variant {name} names an option twice.");
        return problems;
    }

    // ---- maintained fields -----------------------------------------------------------------------------------------

    /// <summary>The facet term of a key and value: <c>colour:red</c> (the value lower-cased and trimmed).</summary>
    public static string FacetTerm(string key, string value) => key + ":" + value.Trim().ToLowerInvariant();

    /// <summary>The facet terms of a product: brand, declared attributes, the active variants' options.</summary>
    public static List<string> FacetValues(ShopProduct product, IEnumerable<ShopVariant> variants)
    {
        var terms = new SortedSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(product.Brand))
            terms.Add(FacetTerm("brand", product.Brand));
        foreach (var attribute in product.Attributes.Where(a => IsKey(a.Name) && !string.IsNullOrWhiteSpace(a.Value)))
            terms.Add(FacetTerm(attribute.Name, attribute.Value));
        foreach (var option in variants.Where(v => v.IsActive).SelectMany(v => v.Options).Where(o => IsKey(o.Name) && !string.IsNullOrWhiteSpace(o.Value)))
            terms.Add(FacetTerm(option.Name, option.Value));
        return [.. terms];
    }

    /// <summary>The searchable texts: every language of the name, the brand, the MPN, the SKUs and GTINs.</summary>
    public static List<string> SearchText(ShopProduct product, IEnumerable<ShopVariant> variants)
    {
        var texts = new List<string>();
        texts.AddRange(product.Name.Entries.Select(e => e.Value));
        if (!string.IsNullOrWhiteSpace(product.Brand))
            texts.Add(product.Brand);
        if (!string.IsNullOrWhiteSpace(product.Mpn))
            texts.Add(product.Mpn);
        foreach (var variant in variants.Where(v => v.IsActive))
        {
            if (!string.IsNullOrWhiteSpace(variant.Sku))
                texts.Add(variant.Sku);
            if (!string.IsNullOrWhiteSpace(variant.Gtin))
                texts.Add(variant.Gtin);
        }
        return texts.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The lowest active variant price (minor units) and its currency.</summary>
    public static (long? Minor, string? Currency) PriceFrom(ShopProduct product, IEnumerable<ShopVariant> variants)
    {
        var prices = variants.Where(v => v.IsActive).Select(v => PriceOf(product, v)).OfType<Money>().ToList();
        if (prices.Count == 0)
            return (null, product.Price?.Currency);
        var lowest = prices.MinBy(p => p.ToMinorUnits())!;
        return (lowest.ToMinorUnits(), lowest.Currency);
    }

    /// <summary>The title of a variant in the basket: the product's name, then its options (<c>T-shirt · M · Red</c>).</summary>
    public static TranslatedString Title(ShopProduct product, ShopVariant variant)
    {
        if (variant.Options.Count == 0)
            return product.Name;
        var suffix = " · " + string.Join(" · ", variant.Options.Select(o => o.Value.Trim()));
        return product.Name.Map((_, value) => value + suffix);
    }

    /// <summary>The variant's main characteristics for the last checkout step (CRD Art. 6(1)(a)): its options and net quantity.</summary>
    public static string Characteristics(ShopProduct product, ShopVariant variant)
    {
        var parts = variant.Options.Select(o => $"{o.Name}: {o.Value}").ToList();
        if (product.UnitOfMeasure != EUnitOfMeasure.None && NetQuantityOf(product, variant) is { } net)
            parts.Add(net.ToString("0.###", CultureInfo.InvariantCulture) + " " + UnitSymbol(product.UnitOfMeasure));
        return string.Join(", ", parts);
    }
}

/// <summary>URL slugs of products and categories.</summary>
public static partial class ShopSlugs
{
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    public static bool IsSlug(string? slug) => slug is { Length: > 0 and <= 120 } && SlugPattern().IsMatch(slug);

    /// <summary>A slug from a name: lower-case ASCII letters and digits, dashes between words.</summary>
    public static string Slugify(string? name, string fallback = "product")
    {
        var normalized = (name ?? "").Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder();
        var dash = false;
        foreach (var c in normalized.ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(c);
                dash = false;
            }
            else if (!dash && builder.Length > 0)
            {
                builder.Append('-');
                dash = true;
            }
        }
        var slug = builder.ToString().Trim('-');
        if (slug.Length > 100)
            slug = slug[..100].Trim('-');
        return slug.Length == 0 ? fallback : slug;
    }
}
