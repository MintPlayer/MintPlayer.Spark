using System.Globalization;
using System.Xml.Linq;

namespace MintPlayer.Spark.Commerce.Invoicing.EInvoicing;

/// <summary>One broken rule: its EN 16931 / Peppol id and a message.</summary>
public sealed record EInvoiceRuleViolation(string Rule, string Message);

/// <summary>
/// Validates a UBL document before it is sent (spike S14). <b>The official artefacts</b> (CEN/TC 434 EN 16931 UBL
/// Schematron, EUPL 1.2; OpenPeppol BIS Billing 3.0 Schematron) are XSLT 2.0, which .NET's <c>XslCompiledTransform</c>
/// (XSLT 1.0) cannot run, and the only .NET XSLT 2.0 processor (Saxon) is not MIT-licensed. So the artefacts are not
/// vendored: this library ships <see cref="En16931RuleSubset"/> (the arithmetic and VAT-category rules the
/// documents this library writes can break), and a host plugs the official validation in through this interface —
/// a phive/Saxon service, the Peppol testbed, or its access point's own validation (both adapters validate on submit).
/// Every registered validator runs; any violation blocks the transmission (the PDF still goes out).
/// </summary>
public interface IEInvoiceValidator
{
    Task<IReadOnlyList<EInvoiceRuleViolation>> ValidateAsync(byte[] ubl, CancellationToken cancellationToken);
}

/// <summary>
/// The EN 16931 rules (CEN/TS 16931-3-2 UBL binding, Peppol BIS Billing 3.0 rule ids) this library checks itself:
/// required parties and addresses (BR-02…BR-11), totals (BR-CO-10, BR-CO-13, BR-CO-14, BR-CO-15, BR-CO-16), the VAT
/// breakdown per category (BR-CO-17 with the Schematron's ±1 tolerance, BR-S-8, BR-AE-*, BR-IC-*, BR-G-*, BR-E-*,
/// BR-O-*), the credit note's preceding-invoice reference and the Peppol electronic addresses (PEPPOL-EN16931-R020/R010).
/// </summary>
public sealed class En16931RuleSubset : IEInvoiceValidator
{
    private static readonly XNamespace Cac = UblWriter.Cac;
    private static readonly XNamespace Cbc = UblWriter.Cbc;

    public Task<IReadOnlyList<EInvoiceRuleViolation>> ValidateAsync(byte[] ubl, CancellationToken cancellationToken)
    {
        XDocument document;
        using (var stream = new MemoryStream(ubl))
            document = XDocument.Load(stream);
        return Task.FromResult(Validate(document.Root!));
    }

    public static IReadOnlyList<EInvoiceRuleViolation> Validate(XElement root)
    {
        var v = new List<EInvoiceRuleViolation>();
        void Fail(string rule, string message) => v.Add(new EInvoiceRuleViolation(rule, message));

        var credit = root.Name.LocalName == "CreditNote";
        var lineName = credit ? "CreditNoteLine" : "InvoiceLine";

        if (Text(root, Cbc + "CustomizationID") != UblWriter.CustomizationId) Fail("BR-01", "Specification identifier missing or not Peppol BIS Billing 3.0.");
        if (string.IsNullOrEmpty(Text(root, Cbc + "ID"))) Fail("BR-02", "Invoice number missing.");
        if (string.IsNullOrEmpty(Text(root, Cbc + "IssueDate"))) Fail("BR-03", "Issue date missing.");
        if (string.IsNullOrEmpty(Text(root, Cbc + (credit ? "CreditNoteTypeCode" : "InvoiceTypeCode")))) Fail("BR-04", "Type code missing.");
        if (string.IsNullOrEmpty(Text(root, Cbc + "DocumentCurrencyCode"))) Fail("BR-05", "Currency missing.");
        if (string.IsNullOrEmpty(Text(root, Cbc + "BuyerReference")) && root.Element(Cac + "OrderReference") is null)
            Fail("PEPPOL-EN16931-R003", "A buyer reference or an order reference is required.");

        var seller = root.Element(Cac + "AccountingSupplierParty")?.Element(Cac + "Party");
        var buyer = root.Element(Cac + "AccountingCustomerParty")?.Element(Cac + "Party");
        if (string.IsNullOrEmpty(seller?.Element(Cac + "PartyLegalEntity")?.Element(Cbc + "RegistrationName")?.Value)) Fail("BR-06", "Seller name missing.");
        if (string.IsNullOrEmpty(buyer?.Element(Cac + "PartyLegalEntity")?.Element(Cbc + "RegistrationName")?.Value)) Fail("BR-07", "Buyer name missing.");
        if (string.IsNullOrEmpty(seller?.Element(Cac + "PostalAddress")?.Element(Cac + "Country")?.Element(Cbc + "IdentificationCode")?.Value)) Fail("BR-09", "Seller country missing.");
        if (string.IsNullOrEmpty(buyer?.Element(Cac + "PostalAddress")?.Element(Cac + "Country")?.Element(Cbc + "IdentificationCode")?.Value)) Fail("BR-11", "Buyer country missing.");
        if (seller?.Element(Cbc + "EndpointID") is null) Fail("PEPPOL-EN16931-R020", "Seller electronic address missing.");
        if (buyer?.Element(Cbc + "EndpointID") is null) Fail("PEPPOL-EN16931-R010", "Buyer electronic address missing.");
        if (credit && root.Element(Cac + "BillingReference") is null) Fail("BR-55", "A credit note references the invoice it credits (BT-25).");

        var lines = root.Elements(Cac + lineName).ToList();
        if (lines.Count == 0) Fail("BR-16", "At least one line.");
        var sumLines = lines.Sum(l => Dec(l, Cbc + "LineExtensionAmount"));

        var totals = root.Element(Cac + "LegalMonetaryTotal");
        var lineExtension = Dec(totals, Cbc + "LineExtensionAmount");
        var taxExclusive = Dec(totals, Cbc + "TaxExclusiveAmount");
        var taxInclusive = Dec(totals, Cbc + "TaxInclusiveAmount");
        var prepaid = Dec(totals, Cbc + "PrepaidAmount");
        var payable = Dec(totals, Cbc + "PayableAmount");
        var taxTotal = root.Elements(Cac + "TaxTotal").FirstOrDefault();
        var taxAmount = Dec(taxTotal, Cbc + "TaxAmount");

        if (sumLines != lineExtension) Fail("BR-CO-10", $"Sum of line net amounts {sumLines} ≠ {lineExtension}.");
        if (taxExclusive != lineExtension) Fail("BR-CO-13", $"Total without VAT {taxExclusive} ≠ line total {lineExtension} (no allowances or charges).");
        if (taxInclusive != taxExclusive + taxAmount) Fail("BR-CO-15", $"Total with VAT {taxInclusive} ≠ {taxExclusive} + {taxAmount}.");
        if (payable != taxInclusive - prepaid) Fail("BR-CO-16", $"Amount due {payable} ≠ {taxInclusive} − {prepaid}.");

        var subtotals = taxTotal?.Elements(Cac + "TaxSubtotal").ToList() ?? [];
        if (subtotals.Sum(s => Dec(s, Cbc + "TaxAmount")) != taxAmount) Fail("BR-CO-14", "Total VAT ≠ sum of the VAT breakdown.");

        var lineCategories = lines.Select(l => (Line: l, Category: l.Element(Cac + "Item")?.Element(Cac + "ClassifiedTaxCategory"))).ToList();
        var codes = subtotals.Select(s => Text(s.Element(Cac + "TaxCategory"), Cbc + "ID") ?? "").ToList();
        var sellerVat = seller?.Element(Cac + "PartyTaxScheme")?.Element(Cbc + "CompanyID")?.Value;
        var buyerVat = buyer?.Element(Cac + "PartyTaxScheme")?.Element(Cbc + "CompanyID")?.Value;
        var buyerLegalId = buyer?.Element(Cac + "PartyLegalEntity")?.Element(Cbc + "CompanyID")?.Value;

        foreach (var subtotal in subtotals)
        {
            var category = subtotal.Element(Cac + "TaxCategory");
            var code = Text(category, Cbc + "ID") ?? "";
            var percentText = Text(category, Cbc + "Percent");
            var percent = percentText is null ? (decimal?)null : decimal.Parse(percentText, CultureInfo.InvariantCulture);
            var taxable = Dec(subtotal, Cbc + "TaxableAmount");
            var tax = Dec(subtotal, Cbc + "TaxAmount");
            var linesOfGroup = lineCategories.Where(x => Text(x.Category, Cbc + "ID") == code && Text(x.Category, Cbc + "Percent") == percentText).Sum(x => Dec(x.Line, Cbc + "LineExtensionAmount"));
            var hasReason = !string.IsNullOrEmpty(Text(category, Cbc + "TaxExemptionReasonCode")) || !string.IsNullOrEmpty(Text(category, Cbc + "TaxExemptionReason"));

            if (code != "O" && percent is { } p)
            {
                // BR-CO-17: VAT = taxable × rate, rounded to 2 decimals; the CEN Schematron accepts ±1.
                var expected = Math.Round(taxable * p / 100m, 2, MidpointRounding.AwayFromZero);
                if (Math.Abs(expected - tax) > 1m) Fail("BR-CO-17", $"{code} {p}%: VAT {tax} ≠ {taxable} × {p}% = {expected}.");
            }
            switch (code)
            {
                case "S":
                    if (taxable != linesOfGroup) Fail("BR-S-8", $"Standard-rated taxable amount {taxable} ≠ its lines {linesOfGroup}.");
                    if (percent is null or <= 0) Fail("BR-S-5", "A standard rate is above zero.");
                    if (string.IsNullOrEmpty(sellerVat)) Fail("BR-S-2", "Seller VAT identifier required for standard-rated lines.");
                    break;
                case "Z":
                    if (percent != 0) Fail("BR-Z-5", "Zero-rated lines have rate 0.");
                    if (tax != 0) Fail("BR-Z-9", "Zero-rated VAT is 0.");
                    break;
                case "AE":
                    if (string.IsNullOrEmpty(sellerVat)) Fail("BR-AE-2", "Seller VAT identifier required for reverse charge.");
                    if (string.IsNullOrEmpty(buyerVat) && string.IsNullOrEmpty(buyerLegalId)) Fail("BR-AE-2", "Buyer VAT identifier or legal registration required for reverse charge.");
                    if (percent != 0) Fail("BR-AE-5", "Reverse-charge rate is 0.");
                    if (tax != 0) Fail("BR-AE-9", "Reverse-charge VAT is 0.");
                    if (taxable != linesOfGroup) Fail("BR-AE-8", "Reverse-charge taxable amount ≠ its lines.");
                    if (!hasReason) Fail("BR-AE-10", "Reverse charge needs an exemption reason (code VATEX-EU-AE or text).");
                    if (codes.Count(c => c == "AE") > 1) Fail("BR-AE-1", "One reverse-charge group.");
                    break;
                case "K":
                    if (string.IsNullOrEmpty(sellerVat) || string.IsNullOrEmpty(buyerVat)) Fail("BR-IC-2", "Seller and buyer VAT identifiers required for an intra-Community supply.");
                    if (percent != 0) Fail("BR-IC-5", "Intra-Community rate is 0.");
                    if (tax != 0) Fail("BR-IC-9", "Intra-Community VAT is 0.");
                    if (!hasReason) Fail("BR-IC-10", "Intra-Community supply needs an exemption reason.");
                    if (root.Element(Cac + "Delivery")?.Element(Cbc + "ActualDeliveryDate") is null && root.Element(Cac + "InvoicePeriod") is null)
                        Fail("BR-IC-11", "Intra-Community supply needs the delivery date or the invoicing period.");
                    if (root.Element(Cac + "Delivery")?.Element(Cac + "DeliveryLocation")?.Element(Cac + "Address")?.Element(Cac + "Country") is null)
                        Fail("BR-IC-12", "Intra-Community supply needs the deliver-to country.");
                    break;
                case "G":
                    if (string.IsNullOrEmpty(sellerVat)) Fail("BR-G-2", "Seller VAT identifier required for an export.");
                    if (percent != 0) Fail("BR-G-5", "Export rate is 0.");
                    if (tax != 0) Fail("BR-G-9", "Export VAT is 0.");
                    if (!hasReason) Fail("BR-G-10", "Export needs an exemption reason.");
                    break;
                case "E":
                    if (percent != 0) Fail("BR-E-5", "Exempt rate is 0.");
                    if (tax != 0) Fail("BR-E-9", "Exempt VAT is 0.");
                    if (!hasReason) Fail("BR-E-10", "Exemption needs a reason.");
                    break;
                case "O":
                    if (codes.Count > 1) Fail("BR-O-11", "A document with a 'not subject to VAT' group holds no other VAT group.");
                    if (!string.IsNullOrEmpty(sellerVat) || !string.IsNullOrEmpty(buyerVat)) Fail("BR-O-2", "'Not subject to VAT' documents carry no seller or buyer VAT identifier.");
                    if (tax != 0) Fail("BR-O-9", "Not-subject VAT is 0.");
                    if (!hasReason) Fail("BR-O-10", "'Not subject to VAT' needs an exemption reason.");
                    break;
            }
        }
        return v;
    }

    private static string? Text(XElement? parent, XName name) => parent?.Element(name)?.Value;

    private static decimal Dec(XElement? parent, XName name)
        => parent?.Element(name)?.Value is { Length: > 0 } s ? decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture) : 0m;
}
