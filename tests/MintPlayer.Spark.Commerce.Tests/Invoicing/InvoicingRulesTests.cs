using System.Text;
using System.Xml.Linq;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Invoicing;
using MintPlayer.Spark.Commerce.Invoicing.EInvoicing;
using MintPlayer.Spark.Commerce.Invoicing.Exports;
using MintPlayer.Spark.Commerce.Invoicing.Models;
using MintPlayer.Spark.Commerce.Invoicing.Pdf;
using MintPlayer.Spark.Commerce.Invoicing.Services;
using MintPlayer.Spark.Commerce.Invoicing.Vat;

namespace MintPlayer.Spark.Commerce.Tests.Invoicing;

/// <summary>Invoicing rules that need no database: S21 rounding, place of supply (GA4), VAT ids, UBL + EN 16931, PDF, CSV.</summary>
public class InvoicingRulesTests
{
    // ---- S21 rounding --------------------------------------------------------------------------------------------------

    [Fact]
    public void S21_net_basis_is_BR_CO_17_exact_whenever_an_integer_net_exists()
    {
        // 121.00 gross at 21 %: net 100.00, VAT 21.00 exactly.
        VatMath.GroupVat(12_100, 2100, InvoicePricingBasis.Net).Should().Be(2_100);
        for (long gross = 1; gross < 5_000; gross++)
        {
            var vat = VatMath.GroupVat(gross, 2100, InvoicePricingBasis.Net);
            var net = gross - vat;
            // Exact when solvable, else within the Schematron's tolerance of one minor unit.
            Math.Abs(VatMath.VatFromNet(net, 2100) - vat).Should().BeLessThanOrEqualTo(1);
        }
    }

    [Fact]
    public void S21_lines_always_sum_to_their_group()
    {
        var gross = new long[] { 999, 1, 2_499, 12_345 };
        var groups = new[] { "S21", "S21", "S21", "S6" };
        var rates = new[] { 2100, 2100, 2100, 600 };
        var vat = VatMath.SplitVat(gross, groups, rates, InvoicePricingBasis.Gross);
        (vat[0] + vat[1] + vat[2]).Should().Be(VatMath.VatFromGross(999 + 1 + 2_499, 2100));
        vat[3].Should().Be(VatMath.VatFromGross(12_345, 600));
    }

    /// <summary>
    /// S21 strategy H, replayed on generated invoices: any sequence of partial credit notes followed by the rest credits
    /// every line exactly (net and VAT), never credits a line beyond the invoice, and keeps each note's group VAT within one
    /// cent of the group's cumulative target.
    /// </summary>
    [Fact]
    public void S21_strategy_H_credit_notes_mirror_the_invoice_and_a_full_refund_cancels_it()
    {
        var random = new Random(521);
        for (var run = 0; run < 2_000; run++)
        {
            var n = random.Next(1, 7);
            var rates = Enumerable.Range(0, n).Select(_ => new[] { 2100, 1200, 600, 0 }[random.Next(4)]).ToArray();
            var gross = Enumerable.Range(0, n).Select(_ => (long)random.Next(1, 50_000)).ToArray();
            var basis = random.Next(2) == 0 ? InvoicePricingBasis.Gross : InvoicePricingBasis.Net;
            var lineVat = VatMath.SplitVat(gross, rates, rates, basis);
            var creditedGross = new long[n];
            var creditedVat = new long[n];
            for (var step = 0; step < 3; step++)
            {
                var refund = Enumerable.Range(0, n).Select(i => step == 2 ? gross[i] - creditedGross[i] : random.Next(0, (int)(gross[i] - creditedGross[i]) + 1) / 2L).ToArray();
                var vat = VatMath.CreditVat(gross, lineVat, rates, refund, creditedGross, creditedVat);
                for (var i = 0; i < n; i++)
                {
                    vat[i].Should().BeGreaterThanOrEqualTo(0);
                    (creditedVat[i] + vat[i]).Should().BeLessThanOrEqualTo(lineVat[i]);
                    creditedGross[i] += refund[i];
                    creditedVat[i] += vat[i];
                }
            }
            creditedGross.Should().Equal(gross);
            creditedVat.Should().Equal(lineVat);
        }
    }

    [Fact]
    public void Cumulative_refunds_credit_only_the_difference_and_returned_lines_first()
    {
        var gross = new long[] { 1_000, 3_000, 500 };
        var targets = VatMath.CumulativeRefund(gross, [false, true, false], [true, true, false], [0, 0, 0], 3_400);
        targets.Should().NotBeNull();
        targets![1].Should().Be(3_000);               // returned line first
        targets[0].Should().Be(400);                  // the rest pro rata over lines that take a share
        targets[2].Should().Be(0);                    // a donation-like line takes no share until the whole order is refunded
        VatMath.CumulativeRefund(gross, [false, false, false], [true, true, true], [1_000, 0, 0], 1_000).Should().BeNull();
    }

    // ---- place of supply (GA4) ---------------------------------------------------------------------------------------

    private static PlaceOfSupplyContext Context(
        VatCategoryKind kind = VatCategoryKind.Standard, bool goods = false, string buyer = "BE", bool business = false, bool verified = false,
        string? shipTo = null, string? eventCountry = null, bool destination = false, string supplier = "BE", bool ioss = false, bool outside = false)
        => new()
        {
            SupplierCountry = supplier, SupplierInEu = EuMemberStates.Contains(supplier), BuyerCountry = buyer, BuyerIsBusiness = business,
            BuyerVatIdVerified = verified, ShipToCountry = shipTo, CategoryKind = kind, IsGoods = goods, EventCountry = eventCountry,
            DistanceSalesAtDestination = destination, IossApplies = ioss, ShipToOutsideVatTerritory = outside,
        };

    private static PlaceOfSupplyDecision Decide(PlaceOfSupplyContext c) => BuiltInPlaceOfSupplyRules.Decide(BuiltInPlaceOfSupplyRules.All, c);

    [Fact]
    public void GA4_admission_is_taxed_where_the_event_takes_place_for_consumers_and_businesses()
    {
        Decide(Context(VatCategoryKind.Admission, buyer: "NL", eventCountry: "FR")).Should().Be(new PlaceOfSupplyDecision("admission", VatTreatment.Taxed, "FR"));
        Decide(Context(VatCategoryKind.Admission, buyer: "NL", business: true, verified: true, eventCountry: "FR")).TaxCountry.Should().Be("FR");
        Decide(Context(VatCategoryKind.Admission, eventCountry: "US")).Treatment.Should().Be(VatTreatment.OutsideScope);
    }

    [Fact]
    public void GA4_B2B_services_across_borders_are_reverse_charged_only_with_a_VIES_valid_id()
    {
        var rc = Decide(Context(buyer: "NL", business: true, verified: true));
        rc.Treatment.Should().Be(VatTreatment.ReverseCharge);
        rc.Mention.Should().Be("Reverse charge");
        Decide(Context(buyer: "NL", business: true, verified: false)).Should().Be(new PlaceOfSupplyDecision("supplier-country", VatTreatment.Taxed, "BE"));
        Decide(Context(buyer: "BE", business: true, verified: true)).TaxCountry.Should().Be("BE");
    }

    [Fact]
    public void GA4_goods_to_a_business_in_another_member_state_are_an_exempt_intra_Community_supply()
        => Decide(Context(goods: true, buyer: "DE", shipTo: "DE", business: true, verified: true)).Treatment.Should().Be(VatTreatment.IntraCommunityExempt);

    [Fact]
    public void GA4_distance_sales_switch_to_the_destination_above_the_threshold()
    {
        Decide(Context(goods: true, buyer: "DE", shipTo: "DE")).Should().Be(new PlaceOfSupplyDecision("distance-sale-below-threshold", VatTreatment.Taxed, "BE"));
        Decide(Context(goods: true, buyer: "DE", shipTo: "DE", destination: true)).Should().Be(new PlaceOfSupplyDecision("distance-sale", VatTreatment.Taxed, "DE"));
    }

    [Fact]
    public void GA4_exports_and_territories_outside_the_VAT_territory_are_zero_rated()
    {
        Decide(Context(goods: true, buyer: "CH", shipTo: "CH")).Treatment.Should().Be(VatTreatment.Export);
        Decide(Context(goods: true, buyer: "ES", shipTo: "ES", outside: true)).Treatment.Should().Be(VatTreatment.Export);
        Decide(Context(goods: true, buyer: "XI", shipTo: "XI")).Treatment.Should().Be(VatTreatment.Taxed); // Northern Ireland: EU for goods
    }

    [Fact]
    public void GC8_IOSS_taxes_a_small_import_by_a_non_EU_seller_at_destination()
    {
        Decide(Context(goods: true, supplier: "US", buyer: "NL", shipTo: "NL", ioss: true)).Should().Be(new PlaceOfSupplyDecision("ioss", VatTreatment.Taxed, "NL"));
        Decide(Context(goods: true, supplier: "US", buyer: "NL", shipTo: "NL")).Treatment.Should().Be(VatTreatment.OutsideScope);
    }

    [Fact]
    public void Host_categories_decide_exempt_and_outside_scope_lines()
    {
        Decide(Context(VatCategoryKind.OutsideScope)).Treatment.Should().Be(VatTreatment.OutsideScope);
        Decide(Context(VatCategoryKind.Exempt)).Treatment.Should().Be(VatTreatment.Exempt);
    }

    // ---- VAT ids and VIES ----------------------------------------------------------------------------------------------

    [Fact]
    public void VAT_ids_are_parsed_per_member_state_shape()
    {
        VatIdFormat.Parse("BE 0123.456.749").Should().Be(new VatId("BE", "0123456749"));
        VatIdFormat.Parse("GR123456789").Should().Be(new VatId("EL", "123456789"));
        VatIdFormat.Parse("NL123456789B01").Should().Be(new VatId("NL", "123456789B01"));
        VatIdFormat.Parse("BE123").Should().BeNull();
        VatIdFormat.Parse("US123456789").Should().BeNull();
    }

    [Fact]
    public void VIES_answers_are_read_from_the_documented_shape()
    {
        var valid = ViesResponseParser.Parse(InvoicingFixtures.Text("Vies", "check-vat-number.valid.json"), 200);
        valid.Valid.Should().BeTrue();
        valid.Checked.Should().BeTrue();
        valid.RequestIdentifier.Should().Be("WAPIAAAAZ1example");
        ViesResponseParser.Parse(InvoicingFixtures.Text("Vies", "check-vat-number.invalid.json"), 200).Valid.Should().BeFalse();
        var down = ViesResponseParser.Parse(InvoicingFixtures.Text("Vies", "check-vat-number.ms-unavailable.json"), 200);
        down.Checked.Should().BeFalse(); // an unavailable member state is no answer: VAT is charged
    }

    // ---- UBL and EN 16931 ----------------------------------------------------------------------------------------------

    internal static Invoice BusinessInvoice(string vatCode, int rate, string? mention = null, string? reasonCode = null, bool goods = false)
    {
        var lines = new List<InvoiceDraftLine>
        {
            new("l1", InvoiceLineSource.Item, "Conference ticket", 2, 24_200, "standard", goods, null, "tickets", new DateOnly(2026, 11, 3), null),
            new("l1#service", InvoiceLineSource.Fee, "Service fee", 1, 121, "standard", false, null, "tickets", null, null),
        };
        var decision = new PlaceOfSupplyDecision("test", vatCode switch
        {
            "AE" => VatTreatment.ReverseCharge,
            "K" => VatTreatment.IntraCommunityExempt,
            "G" => VatTreatment.Export,
            _ => VatTreatment.Taxed,
        }, vatCode == "S" ? "BE" : "NL", mention, reasonCode);
        var decided = lines.Select(_ => new InvoicingService.DecidedLine(decision, rate, vatCode, new VatCategoryOptions())).ToList();
        var invoice = InvoicingService.Assemble(lines, decided, InvoicePricingBasis.Net, "EUR");
        invoice.Kind = InvoiceKind.Invoice;
        invoice.Number = "F2026-000001";
        invoice.IssueDate = new DateOnly(2026, 10, 19);
        invoice.SupplyDate = new DateOnly(2026, 11, 3);
        invoice.OrderNumber = "A1B2C3";
        invoice.Business = true;
        invoice.Issuer = new InvoiceParty { Name = "Example Platform BV", VatId = "BE0123456749", Country = "BE", AddressLines = ["Voorbeeldstraat 1"], PostalCode = "9000", City = "Gent" };
        invoice.Buyer = new InvoiceParty { Name = "Klant BV", VatId = "NL123456789B01", Country = "NL", PeppolId = "0106:12345678", AddressLines = ["Kerkstraat 2"], PostalCode = "1012AB", City = "Amsterdam" };
        invoice.Paid = invoice.TotalGross;
        return invoice;
    }

    [Theory]
    [InlineData("S", 2100, null, null, false)]
    [InlineData("AE", 0, "Reverse charge", "VATEX-EU-AE", false)]
    [InlineData("K", 0, "Exempt intra-Community supply (Art. 138 Directive 2006/112/EC)", "VATEX-EU-IC", true)]
    [InlineData("G", 0, "Exempt export (Art. 146(1) Directive 2006/112/EC)", "VATEX-EU-G", true)]
    public void S14_the_UBL_this_library_writes_passes_the_EN_16931_rule_subset(string code, int rate, string? mention, string? reasonCode, bool goods)
    {
        var invoice = BusinessInvoice(code, rate, mention, reasonCode, goods);
        var ubl = UblWriter.Write(invoice);
        var root = XDocument.Parse(Encoding.UTF8.GetString(ubl)).Root!;
        En16931RuleSubset.Validate(root).Should().BeEmpty();
        root.Name.Should().Be(UblWriter.InvoiceNs + "Invoice");
        root.Element(UblWriter.Cbc + "CustomizationID")!.Value.Should().Be(UblWriter.CustomizationId);
    }

    [Fact]
    public void S14_a_credit_note_references_the_invoice_it_credits()
    {
        var credit = BusinessInvoice("S", 2100);
        credit.Kind = InvoiceKind.CreditNote;
        credit.Number = "C2026-000001";
        credit.CreditedInvoiceNumber = "F2026-000001";
        credit.CreditedInvoiceDate = new DateOnly(2026, 10, 19);
        var root = UblWriter.Build(credit);
        root.Name.Should().Be(UblWriter.CreditNoteNs + "CreditNote");
        root.Element(UblWriter.Cac + "BillingReference")!.Descendants(UblWriter.Cbc + "ID").Single().Value.Should().Be("F2026-000001");
        En16931RuleSubset.Validate(root).Should().BeEmpty();
    }

    [Fact]
    public void S14_the_rule_subset_catches_broken_totals_and_mixed_not_subject_groups()
    {
        var root = UblWriter.Build(BusinessInvoice("S", 2100));
        root.Element(UblWriter.Cac + "LegalMonetaryTotal")!.Element(UblWriter.Cbc + "TaxInclusiveAmount")!.Value = "1.00";
        En16931RuleSubset.Validate(root).Select(v => v.Rule).Should().Contain("BR-CO-15");

        var mixed = BusinessInvoice("S", 2100);
        mixed.VatBreakdown.Add(new InvoiceVatGroup { VatCode = "O", Taxable = Money.Zero("EUR"), Vat = Money.Zero("EUR"), ExemptionReason = "Donation" });
        En16931RuleSubset.Validate(UblWriter.Build(mixed)).Select(v => v.Rule).Should().Contain("BR-O-11");
    }

    [Fact]
    public void S14_reverse_charge_without_the_buyer_VAT_id_is_refused()
    {
        var invoice = BusinessInvoice("AE", 0, "Reverse charge", "VATEX-EU-AE");
        invoice.Buyer.VatId = null;
        invoice.Buyer.CompanyId = null;
        En16931RuleSubset.Validate(UblWriter.Build(invoice)).Select(v => v.Rule).Should().Contain("BR-AE-2");
    }

    [Fact]
    public void Peppol_participant_ids_parse_and_a_Belgian_VAT_id_implies_the_enterprise_number()
    {
        PeppolParticipant.TryParse("0208:0123456749", out var p).Should().BeTrue();
        p.Should().Be(new PeppolParticipant("0208", "0123456749"));
        PeppolParticipant.TryParse("iso6523-actorid-upis::9925:be0123456749", out var q).Should().BeTrue();
        q.Scheme.Should().Be("9925");
        PeppolParticipant.TryParse("BE0123456749", out _).Should().BeFalse();
        PeppolParticipant.FromBelgianVatId("BE0123456749").Should().Be(new PeppolParticipant("0208", "0123456749"));
        PeppolParticipant.FromBelgianVatId("NL123456789B01").Should().BeNull();
    }

    // ---- documents ---------------------------------------------------------------------------------------------------

    [Fact]
    public void An_issued_invoice_hash_detects_any_change()
    {
        var invoice = BusinessInvoice("S", 2100);
        invoice.ContentHash = InvoiceHash.Compute(invoice);
        InvoiceHash.IsIntact(invoice).Should().BeTrue();
        invoice.Buyer.Name = "Someone else";
        InvoiceHash.IsIntact(invoice).Should().BeFalse();
    }

    [Fact]
    public void The_PDF_is_a_PDF_with_the_number_and_the_mentions()
    {
        var invoice = BusinessInvoice("AE", 0, "Reverse charge", "VATEX-EU-AE");
        invoice.Notes.Add("Reverse charge");
        invoice.Language = "nl";
        var pdf = new InvoicePdfRenderer().Render(invoice);
        Encoding.ASCII.GetString(pdf, 0, 8).Should().StartWith("%PDF-1.4");
        var text = Encoding.Latin1.GetString(pdf);
        text.Should().Contain("F2026-000001");
        text.Should().Contain("Reverse charge");
        text.Should().Contain("Factuur");
        text.Should().EndWith("%%EOF\n");
    }

    [Fact]
    public void The_PDF_writer_encodes_WinAnsi_and_escapes_parentheses()
    {
        MinimalPdf.WinAnsi("€ é —").Should().Equal(new byte[] { 0x80, 0x20, 0xE9, 0x20, 0x97 });
        var pdf = new MinimalPdf();
        pdf.Text(10, 10, "a (b) c\\");
        Encoding.Latin1.GetString(pdf.Build()).Should().Contain("(a \\(b\\) c\\\\)");
    }

    [Fact]
    public void CSV_cells_never_run_as_formulas_but_numbers_keep_their_sign()
    {
        var csv = new MintPlayer.Spark.Commerce.Invoicing.Exports.Csv("A", "B").Row("=HYPERLINK(\"x\")", -12.5m).ToString();
        csv.Should().Contain("\"'=HYPERLINK(\"\"x\"\")\"");
        csv.Should().Contain("-12.50");
    }

    // ---- startup validation --------------------------------------------------------------------------------------------

    [Fact]
    public void Startup_refuses_a_setup_without_rates_or_with_a_wrong_provider()
    {
        var empty = new SparkCommerceInvoicingOptions();
        SparkCommerceInvoicingExtensions.Problems(empty, new SparkCommerceOptions(), []).Any(p => p.Contains("No VAT category", StringComparison.Ordinal)).Should().BeTrue();

        var options = InvoicingHost.DefaultOptions();
        options.Peppol.Provider = "nope";
        SparkCommerceInvoicingExtensions.Problems(options, new SparkCommerceOptions(), ["test"]).Any(p => p.Contains("'nope'", StringComparison.Ordinal)).Should().BeTrue();

        options.Peppol.Provider = "test";
        options.Series.CreditNote = options.Series.Invoice;
        SparkCommerceInvoicingExtensions.Problems(options, new SparkCommerceOptions(), ["test"]).Any(p => p.Contains("distinct", StringComparison.Ordinal)).Should().BeTrue();

        SparkCommerceInvoicingExtensions.Problems(InvoicingHost.DefaultOptions(), new SparkCommerceOptions(), ["test"]).Should().BeEmpty();
    }
}
