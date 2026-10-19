using System.Globalization;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Invoicing.Models;

namespace MintPlayer.Spark.Commerce.Invoicing.Pdf;

/// <summary>Renders an issued document as PDF. Replace it to brand the invoices; the default is <see cref="InvoicePdfRenderer"/>.</summary>
public interface IInvoicePdfRenderer
{
    byte[] Render(Invoice invoice);
}

/// <summary>
/// The default invoice layout (A4, Helvetica): the document title and number, the parties with their VAT ids, the lines
/// with net, rate, VAT and gross, the VAT breakdown per rate and country (Art. 226(8)–(10)), the totals and every
/// mention the law requires (Art. 226(11)/(11a), credited document for a credit note). Labels in en, nl, fr and de.
/// </summary>
public sealed class InvoicePdfRenderer : IInvoicePdfRenderer
{
    private const float Left = 50, Right = 545, Bottom = 790;

    public byte[] Render(Invoice invoice)
    {
        var l = Labels.For(invoice.Language);
        var culture = Culture(invoice.Language);
        var pdf = new MinimalPdf();
        float y = 60;

        var title = invoice.Kind switch
        {
            InvoiceKind.Receipt => l.Receipt,
            InvoiceKind.CreditNote or InvoiceKind.FeeCreditNote => l.CreditNote,
            _ => l.Invoice,
        };
        pdf.Text(Left, y, title, 18, bold: true);
        pdf.TextRight(Right, y, invoice.Number, 12, bold: true);
        y += 20;
        pdf.TextRight(Right, y, $"{l.Date}: {Date(invoice.IssueDate)}");
        if (invoice.SupplyDate is { } supply)
            pdf.TextRight(Right, y + 12, $"{l.SupplyDate}: {Date(supply)}");
        if (invoice.OrderNumber is { Length: > 0 } order)
            pdf.TextRight(Right, y + 24, $"{l.Order}: {order}");
        if (invoice.CreditedInvoiceNumber is { Length: > 0 } credited)
            pdf.TextRight(Right, y + 36, $"{l.Credits}: {credited}" + (invoice.CreditedInvoiceDate is { } d ? $" ({Date(d)})" : ""));

        y = Party(pdf, Left, y, l.From, invoice.Issuer, l);
        var buyerY = Party(pdf, 320, 140, l.To, invoice.Buyer, l);
        y = Math.Max(Math.Max(y, buyerY), 200) + 10;

        // Lines.
        float[] col = [Left, 300, 340, 400, 450, Right];
        void Header()
        {
            pdf.Text(col[0], y, l.Description, 9, true);
            pdf.TextRight(col[2], y, l.Quantity, 9, true);
            pdf.TextRight(col[3], y, l.Net, 9, true);
            pdf.TextRight(col[4], y, l.Rate, 9, true);
            pdf.TextRight(col[4] + 45, y, l.Vat, 9, true);
            pdf.TextRight(col[5], y, l.Gross, 9, true);
            pdf.Line(Left, y + 4, Right, y + 4);
            y += 16;
        }
        Header();
        foreach (var line in invoice.Lines)
        {
            var wrapped = MinimalPdf.Wrap(line.Title, col[1] - col[0] + 20).ToList();
            if (y + 12 * wrapped.Count > Bottom)
            {
                pdf.NewPage();
                y = 60;
                Header();
            }
            pdf.TextRight(col[2], y, line.Quantity.ToString(culture));
            pdf.TextRight(col[3], y, Amount(line.Net, culture));
            pdf.TextRight(col[4], y, line.VatCode is "S" or "Z" ? Percent(line.VatRateBasisPoints, culture) : line.VatCode);
            pdf.TextRight(col[4] + 45, y, Amount(line.Vat, culture));
            pdf.TextRight(col[5], y, Amount(line.Gross, culture));
            foreach (var text in wrapped)
            {
                pdf.Text(col[0], y, text);
                y += 12;
            }
        }
        pdf.Line(Left, y - 6, Right, y - 6);
        y += 8;

        // VAT breakdown.
        if (y + 20 + 12 * invoice.VatBreakdown.Count > Bottom)
        {
            pdf.NewPage();
            y = 60;
        }
        pdf.Text(Left, y, l.VatBreakdown, 9, true);
        y += 14;
        foreach (var group in invoice.VatBreakdown)
        {
            var label = (group.VatCode is "S" or "Z" ? Percent(group.RateBasisPoints, culture) : group.VatCode)
                + (group.TaxCountry is { Length: > 0 } c ? " " + c : "");
            pdf.Text(Left, y, label);
            pdf.TextRight(col[3], y, Amount(group.Taxable, culture));
            pdf.TextRight(col[4] + 45, y, Amount(group.Vat, culture));
            if (group.ExemptionReason is { Length: > 0 } reason)
                pdf.Text(col[5] - 90, y, reason.Length > 28 ? reason[..28] + "…" : reason, 7);
            y += 12;
        }
        y += 8;

        // Totals.
        void Total(string label, Money amount, bool bold = false)
        {
            pdf.Text(380, y, label, 10, bold);
            pdf.TextRight(Right, y, Amount(amount, culture) + " " + amount.Currency, 10, bold);
            y += 14;
        }
        Total(l.TotalNet, invoice.TotalNet);
        Total(l.TotalVat, invoice.TotalVat);
        Total(l.Total, invoice.TotalGross, bold: true);
        if (invoice.Paid is { } paid && !invoice.IsCredit)
            Total(l.Paid, paid);
        y += 10;

        foreach (var note in invoice.Notes)
        {
            foreach (var text in MinimalPdf.Wrap(note, Right - Left, 8))
            {
                if (y > Bottom)
                {
                    pdf.NewPage();
                    y = 60;
                }
                pdf.Text(Left, y, text, 8);
                y += 10;
            }
        }
        if (invoice.Issuer.Iban is { Length: > 0 } iban && !invoice.IsCredit && (invoice.Paid is null || invoice.Paid.ToMinorUnits() < invoice.TotalGross.ToMinorUnits()))
            pdf.Text(Left, Math.Min(y + 10, Bottom), $"IBAN {iban}", 8);
        return pdf.Build();
    }

    private static float Party(MinimalPdf pdf, float x, float y, string label, InvoiceParty party, Labels l)
    {
        y += 40;
        pdf.Text(x, y, label, 8, true);
        y += 12;
        pdf.Text(x, y, party.Name, 10, true);
        y += 12;
        foreach (var line in party.AddressLines)
        {
            pdf.Text(x, y, line);
            y += 11;
        }
        var city = string.Join(" ", new[] { party.PostalCode, party.City }.Where(s => s is { Length: > 0 }));
        if (city.Length > 0 || party.Country is { Length: > 0 })
        {
            pdf.Text(x, y, string.Join(", ", new[] { city, party.Country }.Where(s => s is { Length: > 0 })));
            y += 11;
        }
        if (party.VatId is { Length: > 0 } vat)
        {
            pdf.Text(x, y, $"{l.VatId}: {vat}");
            y += 11;
        }
        if (party.CompanyId is { Length: > 0 } company)
        {
            pdf.Text(x, y, $"{l.CompanyId}: {company}");
            y += 11;
        }
        return y;
    }

    private static string Amount(Money money, CultureInfo culture)
        => money.Amount.ToString("N" + Money.MinorUnitDigits(money.Currency).ToString(CultureInfo.InvariantCulture), culture);

    private static string Percent(int basisPoints, CultureInfo culture) => (basisPoints / 100m).ToString("0.##", culture) + " %";

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static CultureInfo Culture(string language)
    {
        try { return CultureInfo.GetCultureInfo(language); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    internal sealed record Labels(
        string Invoice, string Receipt, string CreditNote, string Date, string SupplyDate, string Order, string Credits, string From, string To,
        string Description, string Quantity, string Net, string Rate, string Vat, string Gross, string VatBreakdown,
        string TotalNet, string TotalVat, string Total, string Paid, string VatId, string CompanyId)
    {
        private static readonly Labels En = new("Invoice", "Receipt", "Credit note", "Date", "Date of supply", "Order", "Credits invoice", "From", "To",
            "Description", "Qty", "Net", "Rate", "VAT", "Total", "VAT breakdown", "Total excl. VAT", "VAT", "Total incl. VAT", "Paid", "VAT", "Company no.");
        private static readonly Labels Nl = new("Factuur", "Kasticket", "Creditnota", "Datum", "Leveringsdatum", "Bestelling", "Crediteert factuur", "Van", "Aan",
            "Omschrijving", "Aantal", "Netto", "Tarief", "Btw", "Totaal", "Btw-overzicht", "Totaal excl. btw", "Btw", "Totaal incl. btw", "Betaald", "Btw-nr.", "Ondernemingsnr.");
        private static readonly Labels Fr = new("Facture", "Ticket de caisse", "Note de crédit", "Date", "Date de livraison", "Commande", "Crédite la facture", "De", "À",
            "Description", "Qté", "HT", "Taux", "TVA", "Total", "Ventilation de la TVA", "Total HT", "TVA", "Total TTC", "Payé", "N° TVA", "N° d'entreprise");
        private static readonly Labels De = new("Rechnung", "Quittung", "Gutschrift", "Datum", "Leistungsdatum", "Bestellung", "Gutschrift zu Rechnung", "Von", "An",
            "Beschreibung", "Menge", "Netto", "Satz", "USt.", "Gesamt", "USt.-Aufstellung", "Summe netto", "USt.", "Summe brutto", "Bezahlt", "USt-IdNr.", "Handelsregister");

        public static Labels For(string? language) => (language ?? "en").Split('-')[0].ToLowerInvariant() switch
        {
            "nl" => Nl,
            "fr" => Fr,
            "de" => De,
            _ => En,
        };
    }
}
