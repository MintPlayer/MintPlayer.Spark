using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Invoicing.Vat;

// GA4 (PRD §4.8): which member state's VAT applies to a line, and how. Every rule cites Council Directive 2006/112/EC
// (the VAT Directive). D37: the articles cited were checked against EUR-Lex 02006L0112-20250414 on
// 2026-10-20; national transpositions were not. Admission to an event attended virtually is taxed where the customer is
// (Art. 53 second paragraph, Art. 54(1) second subparagraph), and Monaco counts as France (Art. 7(2)); no rule here
// models either (docs/guide-commerce-compliance.md). Member-state
// options (Art. 59c(3) election, Art. 194 domestic reverse charge, simplified invoices under Art. 220a/238) are host
// configuration, never decided here.

/// <summary>How a line is taxed.</summary>
public enum VatTreatment
{
    /// <summary>Taxed in <see cref="PlaceOfSupplyDecision.TaxCountry"/> at its rate (UNCL 5305 <c>S</c>, or <c>Z</c> at 0 %).</summary>
    Taxed,
    /// <summary>The customer accounts for the VAT (Art. 196): <c>AE</c>, 0 %, mention "Reverse charge" (Art. 226(11a)).</summary>
    ReverseCharge,
    /// <summary>Exempt intra-Community supply of goods (Art. 138(1)): <c>K</c>, 0 %.</summary>
    IntraCommunityExempt,
    /// <summary>Exempt export outside the EU (Art. 146(1)(a)/(b)): <c>G</c>, 0 %, proof of export kept (GB6).</summary>
    Export,
    /// <summary>Exempt by the host's category (<c>E</c>).</summary>
    Exempt,
    /// <summary>Outside the scope of EU VAT (<c>O</c>): no VAT breakdown rate.</summary>
    OutsideScope,
}

/// <summary>The facts a rule decides on, per line.</summary>
public sealed record PlaceOfSupplyContext
{
    /// <summary>The supplier's member state of establishment (or a third country).</summary>
    public required string SupplierCountry { get; init; }

    public required bool SupplierInEu { get; init; }

    /// <summary>The customer's country (billing), or null when unknown (a B2C receipt without address).</summary>
    public string? BuyerCountry { get; init; }

    /// <summary>The customer is a taxable person (a business with a VAT id).</summary>
    public bool BuyerIsBusiness { get; init; }

    /// <summary>The customer's VAT id was valid in VIES at issue (Art. 138(1)(b) as amended by Directive (EU) 2018/1910).</summary>
    public bool BuyerVatIdVerified { get; init; }

    /// <summary>Where goods are dispatched to (end of transport, Art. 33(a)); null for services or pickup.</summary>
    public string? ShipToCountry { get; init; }

    /// <summary>The ship-to address lies in a territory outside the EU VAT territory (Art. 6).</summary>
    public bool ShipToOutsideVatTerritory { get; init; }

    /// <summary>The billing address lies in such a territory.</summary>
    public bool BuyerOutsideVatTerritory { get; init; }

    public required VatCategoryKind CategoryKind { get; init; }

    public required bool IsGoods { get; init; }

    /// <summary>Where the event takes place, for admission lines (Art. 53/54(1)).</summary>
    public string? EventCountry { get; init; }

    /// <summary>Art. 59c: the supplier's EU-wide distance sales exceed EUR 10 000 (this year with this supply, or last
    /// year), or it opted for destination taxation (Art. 59c(3)).</summary>
    public bool DistanceSalesAtDestination { get; init; }

    /// <summary>GC8: an IOSS number applies and the consignment's intrinsic value is at most EUR 150 (Art. 369l).</summary>
    public bool IossApplies { get; init; }

    /// <summary>The line's EU membership test; the host may widen it (the list is the EU-27 plus <c>XI</c> for goods).</summary>
    public Func<string?, bool> IsEuMemberState { get; init; } = EuMemberStates.Contains;
}

/// <summary>A rule's answer.</summary>
/// <param name="Rule">The rule's code, stored on the line.</param>
/// <param name="Treatment">How the line is taxed.</param>
/// <param name="TaxCountry">The member state whose rate applies (for <see cref="VatTreatment.Taxed"/>), else the reporting one.</param>
/// <param name="Mention">The invoice mention the law requires (Art. 226(11)/(11a)); null for none.</param>
/// <param name="ExemptionReasonCode">The VATEX code (BT-121).</param>
public sealed record PlaceOfSupplyDecision(string Rule, VatTreatment Treatment, string? TaxCountry, string? Mention = null, string? ExemptionReasonCode = null);

/// <summary>
/// Picks the place of supply of a line (GA4). Rules run by <see cref="Order"/>; the first that answers wins. A host adds
/// its own (an electronic-service rule under Art. 58, a domestic reverse charge under Art. 194) by registering one
/// (<c>[SparkDiscoverable]</c>, scoped). The built-in rules are listed in the guide with their articles.
/// </summary>
[SparkDiscoverable]
public interface IPlaceOfSupplyRule
{
    int Order { get; }

    PlaceOfSupplyDecision? Decide(PlaceOfSupplyContext context);
}

/// <summary>The EU-27 by ISO code (Greece is <c>GR</c> here; VIES uses <c>EL</c>), plus <c>XI</c> (Northern Ireland, goods only).</summary>
public static class EuMemberStates
{
    private static readonly HashSet<string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AT", "BE", "BG", "CY", "CZ", "DE", "DK", "EE", "ES", "FI", "FR", "GR", "HR", "HU", "IE", "IT", "LT", "LU", "LV",
        "MT", "NL", "PL", "PT", "RO", "SE", "SI", "SK",
    };

    public static bool Contains(string? country) => country is { Length: 2 } && Codes.Contains(country);

    /// <summary>Northern Ireland is treated as a member state for goods (Windsor Framework, Protocol Art. 8).</summary>
    public static bool ContainsForGoods(string? country) => Contains(country) || string.Equals(country, "XI", StringComparison.OrdinalIgnoreCase);

    /// <summary>The VIES prefix of a country (Greece: <c>EL</c>).</summary>
    public static string ViesPrefix(string country) => string.Equals(country, "GR", StringComparison.OrdinalIgnoreCase) ? "EL" : country.ToUpperInvariant();
}

/// <summary>The built-in rules (GA4). Order leaves room between them for a host's own.</summary>
public static class BuiltInPlaceOfSupplyRules
{
    public static IReadOnlyList<IPlaceOfSupplyRule> All { get; } =
    [
        new CategoryRule(), new AdmissionRule(), new ImportOneStopShopRule(), new ExportRule(),
        new IntraCommunityGoodsRule(), new BusinessServicesRule(), new DistanceSalesRule(), new SupplierCountryRule(),
    ];

    /// <summary>Exempt and outside-scope categories (host-defined): the supplier's country reports them.</summary>
    public sealed class CategoryRule : IPlaceOfSupplyRule
    {
        public int Order => 100;

        public PlaceOfSupplyDecision? Decide(PlaceOfSupplyContext c) => c.CategoryKind switch
        {
            VatCategoryKind.OutsideScope => new("outside-scope", VatTreatment.OutsideScope, null),
            VatCategoryKind.Exempt => new("exempt", VatTreatment.Exempt, c.SupplierCountry),
            _ => null,
        };
    }

    /// <summary>
    /// Admission to cultural, artistic, sporting, scientific, educational, entertainment or similar events: where the
    /// event actually takes place, for businesses (Art. 53) and consumers (Art. 54(1)) alike, so admission is never
    /// reverse-charged by this rule (a member state's Art. 194 option is a host rule). An event outside the EU is
    /// outside the scope of EU VAT. Research §7.6.
    /// </summary>
    public sealed class AdmissionRule : IPlaceOfSupplyRule
    {
        public int Order => 200;

        public PlaceOfSupplyDecision? Decide(PlaceOfSupplyContext c)
        {
            if (c.CategoryKind != VatCategoryKind.Admission)
                return null;
            var where = c.EventCountry ?? c.SupplierCountry;
            return c.IsEuMemberState(where)
                ? new("admission", VatTreatment.Taxed, where)
                : new("admission", VatTreatment.OutsideScope, null, "Outside the scope of EU VAT: the event takes place outside the EU (Art. 53/54 Directive 2006/112/EC)");
        }
    }

    /// <summary>
    /// GC8: goods imported into the EU in a consignment of at most EUR 150 under IOSS: taxed in the member state of
    /// arrival (Art. 33(c)), declared through IOSS (Art. 369l–369x); the IOSS number goes on the invoice and the
    /// customs declaration (GB6).
    /// </summary>
    public sealed class ImportOneStopShopRule : IPlaceOfSupplyRule
    {
        public int Order => 300;

        public PlaceOfSupplyDecision? Decide(PlaceOfSupplyContext c)
        {
            if (!c.IsGoods || !c.IossApplies || c.SupplierInEu || c.BuyerIsBusiness || c.ShipToOutsideVatTerritory)
                return null;
            var destination = c.ShipToCountry ?? c.BuyerCountry;
            return c.IsEuMemberState(destination) ? new("ioss", VatTreatment.Taxed, destination) : null;
        }
    }

    /// <summary>
    /// Goods dispatched outside the EU, or into a territory outside the EU VAT territory (Art. 6): exempt export
    /// (Art. 146(1)(a)/(b)) with proof of export (GB6). Services to a customer outside the EU: outside the scope
    /// (B2B Art. 44; B2C Art. 59 for the services it lists — a host rule narrows this).
    /// </summary>
    public sealed class ExportRule : IPlaceOfSupplyRule
    {
        public int Order => 400;

        public PlaceOfSupplyDecision? Decide(PlaceOfSupplyContext c)
        {
            if (c.IsGoods)
            {
                var destination = c.ShipToCountry;
                if (destination is null || !c.SupplierInEu)
                    return null;
                return c.ShipToOutsideVatTerritory || !EuMemberStates.ContainsForGoods(destination)
                    ? new("export", VatTreatment.Export, c.SupplierCountry, "Exempt export (Art. 146(1) Directive 2006/112/EC)", "VATEX-EU-G")
                    : null;
            }
            if (c.BuyerIsBusiness && c.BuyerCountry is { } country && (!c.IsEuMemberState(country) || c.BuyerOutsideVatTerritory))
                return new("services-outside-eu", VatTreatment.OutsideScope, null, "Outside the scope of EU VAT (Art. 44 Directive 2006/112/EC)", "VATEX-EU-O");
            return null;
        }
    }

    /// <summary>
    /// Goods dispatched to a business in another member state with a VIES-valid VAT id: exempt intra-Community supply
    /// (Art. 138(1); the valid id is a substantive condition since Directive (EU) 2018/1910). Unverified: taxed as
    /// domestic (the later rules decide), never exempted on trust.
    /// </summary>
    public sealed class IntraCommunityGoodsRule : IPlaceOfSupplyRule
    {
        public int Order => 500;

        public PlaceOfSupplyDecision? Decide(PlaceOfSupplyContext c)
        {
            var destination = c.ShipToCountry ?? c.BuyerCountry;
            if (!c.IsGoods || !c.BuyerIsBusiness || !c.BuyerVatIdVerified || !c.SupplierInEu || destination is null
                || !EuMemberStates.ContainsForGoods(destination) || string.Equals(destination, c.SupplierCountry, StringComparison.OrdinalIgnoreCase))
                return null;
            return new("intra-community-goods", VatTreatment.IntraCommunityExempt, destination,
                "Exempt intra-Community supply (Art. 138 Directive 2006/112/EC)", "VATEX-EU-IC");
        }
    }

    /// <summary>
    /// Services to a business in another member state: the place is the customer's (Art. 44) and the customer
    /// reverse-charges the VAT (Art. 196), with the mention "Reverse charge" (Art. 226(11a)). Needs a VIES-valid id;
    /// without one the supplier taxes as B2C (Art. 45), which is the cautious reading (Implementing Regulation
    /// 282/2011 Art. 18 lets a supplier treat an unverified customer as a non-taxable person).
    /// </summary>
    public sealed class BusinessServicesRule : IPlaceOfSupplyRule
    {
        public int Order => 600;

        public PlaceOfSupplyDecision? Decide(PlaceOfSupplyContext c)
        {
            if (c.IsGoods || !c.BuyerIsBusiness || !c.BuyerVatIdVerified || !c.SupplierInEu || c.BuyerCountry is not { } country
                || !c.IsEuMemberState(country) || string.Equals(country, c.SupplierCountry, StringComparison.OrdinalIgnoreCase))
                return null;
            return new("reverse-charge", VatTreatment.ReverseCharge, country, "Reverse charge", "VATEX-EU-AE");
        }
    }

    /// <summary>
    /// Intra-Community distance sales of goods to consumers: the member state where the transport ends (Art. 33(a)) once
    /// the supplier's EU-wide total exceeds EUR 10 000 in the current or the preceding year, or by its option
    /// (Art. 59c(1)–(3)); below it, the supplier's member state. Declared through the Union OSS (Art. 369a–369k).
    /// </summary>
    public sealed class DistanceSalesRule : IPlaceOfSupplyRule
    {
        public int Order => 700;

        public PlaceOfSupplyDecision? Decide(PlaceOfSupplyContext c)
        {
            var destination = c.ShipToCountry;
            if (!c.IsGoods || c.BuyerIsBusiness || !c.SupplierInEu || destination is null || !c.IsEuMemberState(destination)
                || string.Equals(destination, c.SupplierCountry, StringComparison.OrdinalIgnoreCase))
                return null;
            return c.DistanceSalesAtDestination
                ? new("distance-sale", VatTreatment.Taxed, destination)
                : new("distance-sale-below-threshold", VatTreatment.Taxed, c.SupplierCountry);
        }
    }

    /// <summary>Everything else: the supplier's member state (B2C services Art. 45, domestic sales).</summary>
    public sealed class SupplierCountryRule : IPlaceOfSupplyRule
    {
        public int Order => 1000;

        public PlaceOfSupplyDecision? Decide(PlaceOfSupplyContext c)
            => c.SupplierInEu
                ? new("supplier-country", VatTreatment.Taxed, c.SupplierCountry)
                : new("outside-eu-supplier", VatTreatment.OutsideScope, null);
    }

    /// <summary>Runs <paramref name="rules"/> by order; the first answer wins.</summary>
    public static PlaceOfSupplyDecision Decide(IEnumerable<IPlaceOfSupplyRule> rules, PlaceOfSupplyContext context)
    {
        foreach (var rule in rules.OrderBy(r => r.Order))
        {
            if (rule.Decide(context) is { } decision)
                return decision;
        }
        return new SupplierCountryRule().Decide(context)!;
    }
}
