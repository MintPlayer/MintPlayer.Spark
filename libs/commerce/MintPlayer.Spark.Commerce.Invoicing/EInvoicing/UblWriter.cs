using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Invoicing.Models;

namespace MintPlayer.Spark.Commerce.Invoicing.EInvoicing;

/// <summary>
/// Writes an <see cref="Invoice"/> as UBL 2.1 conforming to EN 16931 and Peppol BIS Billing 3.0 (§4.8): an
/// <c>Invoice</c> (type 380) or a <c>CreditNote</c> (type 381) whose lines mirror the credited invoice. Element order
/// follows the UBL 2.1 schema; business terms (BT-…) are named in comments where the mapping is not obvious.
/// See <see href="https://docs.peppol.eu/poacc/billing/3.0/"/>.
/// </summary>
public static class UblWriter
{
    public const string CustomizationId = "urn:cen.eu:en16931:2017#compliant#urn:fdc:peppol.eu:2017:poacc:billing:3.0";
    public const string ProfileId = "urn:fdc:peppol.eu:2017:poacc:billing:01:1.0";
    public const string InvoiceDocumentType = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2::Invoice##" + CustomizationId + "::2.1";
    public const string CreditNoteDocumentType = "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2::CreditNote##" + CustomizationId + "::2.1";

    public static readonly XNamespace InvoiceNs = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    public static readonly XNamespace CreditNoteNs = "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2";
    public static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    public static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    /// <summary>The UBL bytes (UTF-8, no BOM).</summary>
    public static byte[] Write(Invoice invoice)
    {
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), Build(invoice));
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, Async = false }))
            document.Save(writer);
        return stream.ToArray();
    }

    public static XElement Build(Invoice invoice)
    {
        var credit = invoice.IsCredit;
        var root = credit ? CreditNoteNs : InvoiceNs;
        var currency = invoice.Currency;
        var e = new XElement(root + (credit ? "CreditNote" : "Invoice"),
            new XAttribute(XNamespace.Xmlns + "cac", Cac.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "cbc", Cbc.NamespaceName));

        e.Add(new XElement(Cbc + "CustomizationID", CustomizationId));
        e.Add(new XElement(Cbc + "ProfileID", ProfileId));
        e.Add(new XElement(Cbc + "ID", invoice.Number));
        e.Add(new XElement(Cbc + "IssueDate", Date(invoice.IssueDate)));
        e.Add(new XElement(Cbc + (credit ? "CreditNoteTypeCode" : "InvoiceTypeCode"), credit ? "381" : "380"));
        foreach (var note in invoice.Notes)
            e.Add(new XElement(Cbc + "Note", note));
        e.Add(new XElement(Cbc + "DocumentCurrencyCode", currency));
        // BT-10 buyer reference or BT-13 order reference is required by Peppol (PEPPOL-EN16931-R003): the order number.
        e.Add(new XElement(Cbc + "BuyerReference", invoice.OrderNumber ?? invoice.Number));
        if (invoice.OrderNumber is { Length: > 0 } orderNumber)
            e.Add(new XElement(Cac + "OrderReference", new XElement(Cbc + "ID", orderNumber)));
        if (credit && invoice.CreditedInvoiceNumber is { Length: > 0 } credited)
        {
            // BG-3 preceding invoice reference (BT-25, BT-26).
            var reference = new XElement(Cac + "InvoiceDocumentReference", new XElement(Cbc + "ID", credited));
            if (invoice.CreditedInvoiceDate is { } creditedDate)
                reference.Add(new XElement(Cbc + "IssueDate", Date(creditedDate)));
            e.Add(new XElement(Cac + "BillingReference", reference));
        }
        e.Add(new XElement(Cac + "AccountingSupplierParty", Party(invoice.Issuer, seller: true)));
        e.Add(new XElement(Cac + "AccountingCustomerParty", Party(invoice.Buyer, seller: false)));

        // BG-13 delivery: the date of supply (BT-72) and, for an intra-Community supply, the deliver-to country (BR-IC-12).
        if (invoice.SupplyDate is not null || invoice.Lines.Any(l => l.VatCode == "K"))
        {
            var delivery = new XElement(Cac + "Delivery");
            if (invoice.SupplyDate is { } date)
                delivery.Add(new XElement(Cbc + "ActualDeliveryDate", Date(date)));
            var deliverTo = invoice.Lines.FirstOrDefault(l => l.VatCode == "K")?.TaxCountry ?? invoice.Buyer.Country;
            if (deliverTo is { Length: 2 })
                delivery.Add(new XElement(Cac + "DeliveryLocation", new XElement(Cac + "Address", new XElement(Cac + "Country", new XElement(Cbc + "IdentificationCode", Country(deliverTo))))));
            e.Add(delivery);
        }

        if (!credit && invoice.Issuer.Iban is { Length: > 0 } iban && (invoice.Paid is null || invoice.Paid.ToMinorUnits() < invoice.TotalGross.ToMinorUnits()))
        {
            e.Add(new XElement(Cac + "PaymentMeans",
                new XElement(Cbc + "PaymentMeansCode", "58"), // SEPA credit transfer (UNCL 4461)
                new XElement(Cac + "PayeeFinancialAccount", new XElement(Cbc + "ID", iban.Replace(" ", "", StringComparison.Ordinal)))));
        }
        if (invoice.PaymentTerms is { Length: > 0 } terms)
            e.Add(new XElement(Cac + "PaymentTerms", new XElement(Cbc + "Note", terms)));

        var taxTotal = new XElement(Cac + "TaxTotal", Amount("TaxAmount", invoice.TotalVat));
        foreach (var group in invoice.VatBreakdown)
        {
            taxTotal.Add(new XElement(Cac + "TaxSubtotal",
                Amount("TaxableAmount", group.Taxable),
                Amount("TaxAmount", group.Vat),
                TaxCategory("TaxCategory", group.VatCode, group.RateBasisPoints, group.ExemptionReasonCode, group.ExemptionReason)));
        }
        e.Add(taxTotal);

        var paid = invoice.Paid ?? Money.Zero(currency);
        var payable = invoice.TotalGross - paid;
        e.Add(new XElement(Cac + "LegalMonetaryTotal",
            Amount("LineExtensionAmount", invoice.TotalNet),
            Amount("TaxExclusiveAmount", invoice.TotalNet),
            Amount("TaxInclusiveAmount", invoice.TotalGross),
            Amount("PrepaidAmount", paid),
            Amount("PayableAmount", payable)));

        foreach (var line in invoice.Lines)
        {
            var quantity = Math.Max(1, line.Quantity);
            var price = Math.Round(line.Net.Amount / quantity, 4, MidpointRounding.AwayFromZero);
            e.Add(new XElement(Cac + (credit ? "CreditNoteLine" : "InvoiceLine"),
                new XElement(Cbc + "ID", line.Position.ToString(CultureInfo.InvariantCulture)),
                new XElement(Cbc + (credit ? "CreditedQuantity" : "InvoicedQuantity"), new XAttribute("unitCode", "C62"), quantity.ToString(CultureInfo.InvariantCulture)),
                Amount("LineExtensionAmount", line.Net),
                new XElement(Cac + "Item",
                    new XElement(Cbc + "Name", Truncate(line.Title, 200)),
                    TaxCategory("ClassifiedTaxCategory", line.VatCode, line.VatRateBasisPoints, null, null)),
                new XElement(Cac + "Price",
                    new XElement(Cbc + "PriceAmount", new XAttribute("currencyID", currency), price.ToString("0.00##", CultureInfo.InvariantCulture)))));
        }
        return e;
    }

    private static XElement Party(InvoiceParty party, bool seller)
    {
        var p = new XElement(Cac + "Party");
        // BT-34 / BT-49 electronic address (required by Peppol): the participant id, else the Belgian enterprise number
        // a BE VAT id implies (ICD 0208); other schemes are never guessed.
        if (PeppolParticipant.TryParse(party.PeppolId, out var participant))
            p.Add(new XElement(Cbc + "EndpointID", new XAttribute("schemeID", participant.Scheme), participant.Identifier));
        else if (PeppolParticipant.FromBelgianVatId(party.VatId) is { } derived)
            p.Add(new XElement(Cbc + "EndpointID", new XAttribute("schemeID", derived.Scheme), derived.Identifier));
        if (party.Name is { Length: > 0 })
            p.Add(new XElement(Cac + "PartyName", new XElement(Cbc + "Name", party.Name)));
        var address = new XElement(Cac + "PostalAddress");
        if (party.AddressLines.Count > 0)
            address.Add(new XElement(Cbc + "StreetName", party.AddressLines[0]));
        if (party.AddressLines.Count > 1)
            address.Add(new XElement(Cbc + "AdditionalStreetName", string.Join(", ", party.AddressLines.Skip(1))));
        if (party.City is { Length: > 0 })
            address.Add(new XElement(Cbc + "CityName", party.City));
        if (party.PostalCode is { Length: > 0 })
            address.Add(new XElement(Cbc + "PostalZone", party.PostalCode));
        if (party.Country is { Length: 2 } country)
            address.Add(new XElement(Cac + "Country", new XElement(Cbc + "IdentificationCode", Country(country))));
        p.Add(address);
        if (party.VatId is { Length: > 0 } vat)
            p.Add(new XElement(Cac + "PartyTaxScheme", new XElement(Cbc + "CompanyID", vat), new XElement(Cac + "TaxScheme", new XElement(Cbc + "ID", "VAT"))));
        var legal = new XElement(Cac + "PartyLegalEntity", new XElement(Cbc + "RegistrationName", party.Name is { Length: > 0 } n ? n : "-"));
        if (party.CompanyId is { Length: > 0 } companyId)
            legal.Add(new XElement(Cbc + "CompanyID", companyId));
        p.Add(legal);
        if (seller && party.Email is { Length: > 0 } email)
            p.Add(new XElement(Cac + "Contact", new XElement(Cbc + "ElectronicMail", email)));
        return p;
    }

    private static XElement TaxCategory(string name, string code, int rateBasisPoints, string? reasonCode, string? reason)
    {
        var c = new XElement(Cac + name, new XElement(Cbc + "ID", code));
        // "Not subject to VAT" carries no rate (BR-O-5/BR-O-6); every other category does (0 for the exempt ones).
        if (code != "O")
            c.Add(new XElement(Cbc + "Percent", (rateBasisPoints / 100m).ToString("0.##", CultureInfo.InvariantCulture)));
        if (name == "TaxCategory" && code is "E" or "AE" or "K" or "G" or "O")
        {
            if (reasonCode is { Length: > 0 })
                c.Add(new XElement(Cbc + "TaxExemptionReasonCode", reasonCode));
            if (reason is { Length: > 0 })
                c.Add(new XElement(Cbc + "TaxExemptionReason", reason));
        }
        c.Add(new XElement(Cac + "TaxScheme", new XElement(Cbc + "ID", "VAT")));
        return c;
    }

    private static XElement Amount(string name, Money money)
        => new(Cbc + name, new XAttribute("currencyID", money.Currency), money.Amount.ToString("0.00", CultureInfo.InvariantCulture));

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>EN 16931 uses ISO 3166-1: Greece is <c>GR</c>, Northern Ireland <c>XI</c> is accepted for goods.</summary>
    private static string Country(string code) => code.ToUpperInvariant() == "EL" ? "GR" : code.ToUpperInvariant();

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
