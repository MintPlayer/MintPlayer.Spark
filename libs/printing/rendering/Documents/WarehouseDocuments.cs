using System.Globalization;
using MintPlayer.Spark.Printing.Rendering.Barcodes;
using MintPlayer.Spark.Printing.Rendering.Pdf;

namespace MintPlayer.Spark.Printing.Rendering.Documents;

/// <summary>A line of a packing slip.</summary>
public sealed record PackingSlipLine(string Sku, string Name, decimal Quantity, string? LotCode = null);

/// <summary>
/// The packing slip (PRD §4.7, Packing step 6): A4, the order number as Code 128, the ship-to address, the lines, and the
/// texts the host passes in its language (headings are parameters, so the warehouse translates them, not this package).
/// </summary>
public sealed record PackingSlip(
    string OrderNumber,
    IReadOnlyList<string> ShipTo,
    IReadOnlyList<PackingSlipLine> Lines,
    string Title = "Packing slip",
    string? ShippedOnText = null,
    string SkuHeading = "SKU",
    string NameHeading = "Item",
    string QuantityHeading = "Qty",
    string LotHeading = "Lot",
    string? Footer = null)
{
    public byte[] Render()
    {
        var pdf = new PdfWriter(PdfPageSize.A4);
        const float left = 40;
        var y = Header(pdf, left);
        foreach (var line in Lines)
        {
            if (y > 780)
            {
                pdf.NewPage();
                y = Header(pdf, left);
            }
            pdf.Text(left, y, line.Sku, 9);
            pdf.Text(left + 120, y, Clip(line.Name, 300, 9), 9);
            pdf.Text(left + 430, y, line.Quantity.ToString("0.##", CultureInfo.InvariantCulture), 9);
            if (line.LotCode is not null)
                pdf.Text(left + 470, y, Clip(line.LotCode, 75, 9), 9);
            y += 14;
        }
        if (Footer is not null)
            foreach (var text in PdfWriter.Wrap(Footer, 515, 8))
            {
                y += 11;
                pdf.Text(left, y, text, 8);
            }
        return pdf.Build();
    }

    private float Header(PdfWriter pdf, float left)
    {
        pdf.Text(left, 60, Title, 18, bold: true);
        pdf.Text(left, 80, OrderNumber, 11, bold: true);
        if (ShippedOnText is not null)
            pdf.Text(left, 95, ShippedOnText, 9);
        pdf.Barcode(360, 40, Barcode.Code128(OrderNumber), 40, 1.2f);
        var y = 125f;
        foreach (var line in ShipTo.Take(6))
        {
            pdf.Text(left, y, line, 10);
            y += 13;
        }
        y += 15;
        pdf.Text(left, y, SkuHeading, 9, bold: true);
        pdf.Text(left + 120, y, NameHeading, 9, bold: true);
        pdf.Text(left + 430, y, QuantityHeading, 9, bold: true);
        pdf.Text(left + 470, y, LotHeading, 9, bold: true);
        pdf.Line(left, y + 4, 555, y + 4);
        return y + 18;
    }

    internal static string Clip(string text, float width, float size)
    {
        if (PdfWriter.Width(text, size) <= width)
            return text;
        while (text.Length > 1 && PdfWriter.Width(text + "…", size) > width)
            text = text[..^1];
        return text + "…";
    }
}

/// <summary>A row of a paper pick list.</summary>
public sealed record PickListRow(string LocationCode, string Sku, string Name, decimal Quantity, string? LotCode = null, string? Tote = null);

/// <summary>The paper pick list, the fallback when handhelds are down (PRD §4.11): A4, rows in walk-sequence order, a tick box per row.</summary>
public sealed record PickList(string Title, IReadOnlyList<PickListRow> Rows, string? Subtitle = null, string? BatchBarcode = null)
{
    public byte[] Render()
    {
        var pdf = new PdfWriter(PdfPageSize.A4);
        var page = 1;
        var y = Header(pdf, page);
        foreach (var row in Rows)
        {
            if (y > 800)
            {
                pdf.NewPage();
                y = Header(pdf, ++page);
            }
            pdf.Frame(40, y - 9, 10, 10);
            pdf.Text(58, y, row.LocationCode, 10, bold: true);
            pdf.Text(150, y, row.Sku, 9);
            pdf.Text(240, y, PackingSlip.Clip(row.Name, 200, 9), 9);
            pdf.Text(450, y, row.Quantity.ToString("0.##", CultureInfo.InvariantCulture), 10, bold: true);
            pdf.Text(485, y, PackingSlip.Clip(string.Join(" ", new[] { row.LotCode, row.Tote }.Where(t => t is not null)), 70, 8), 8);
            y += 18;
        }
        return pdf.Build();
    }

    private float Header(PdfWriter pdf, int page)
    {
        pdf.Text(40, 55, Title, 16, bold: true);
        if (Subtitle is not null)
            pdf.Text(40, 72, Subtitle, 9);
        pdf.Text(520, 55, page.ToString(CultureInfo.InvariantCulture), 9);
        if (BatchBarcode is not null)
            pdf.Barcode(330, 35, Barcode.Code128(BatchBarcode), 30, 1f);
        pdf.Line(40, 90, 555, 90);
        return 110;
    }
}
