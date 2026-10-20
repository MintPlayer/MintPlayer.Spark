using System.Globalization;
using MintPlayer.Spark.Printing.Rendering.Barcodes;

namespace MintPlayer.Spark.Printing.Rendering.Labels;

// The label set (PRD §4.11, WX6): locations, items and cases, totes, SSCC logistic labels. Each is a template filled with
// its data; LabelRenderer turns it into ZPL (203/300 dpi) or a PDF of the label's size. Texts are passed in already
// formatted: dates on labels are DateOnly expiries (ISO, no zone), and anything else the caller formats in the reader's
// locale (commerce X14, X16).

/// <summary>A location label: the code large, the check digits boxed (keyed in when the barcode will not scan), Code 128.</summary>
public sealed record LocationLabel(string Code, string CheckDigits, string? Barcode = null) : ILabelTemplate
{
    public LabelSize Size => LabelSize.Small;

    public void Draw(ILabelCanvas canvas)
    {
        canvas.Text(4, 4, 9, Code, bold: true);
        canvas.Box(80, 3, 17, 13, 0.5f);
        canvas.Text(83, 6, 7, CheckDigits, bold: true);
        canvas.Barcode(4, 20, Barcodes.Barcode.Code128(Barcode ?? Code), 26, 0.375f);
    }
}

/// <summary>
/// An item or case label. With a GTIN, a GS1-128 symbol with the GTIN and, when given, the lot (10), the expiry (17) and
/// the count (30, a case's contents); otherwise a Code 128 of the SKU.
/// </summary>
public sealed record ItemLabel(string Sku, string Name, string? Gtin = null, string? LotCode = null, DateOnly? Expiry = null,
    decimal? Count = null) : ILabelTemplate
{
    public LabelSize Size => LabelSize.Small;

    public void Draw(ILabelCanvas canvas)
    {
        canvas.Text(4, 3, 6, Sku, bold: true);
        canvas.Text(4, 11, 3.5f, Truncate(Name, 48));
        var details = string.Join("   ", new[]
        {
            LotCode is null ? null : $"LOT {LotCode}",
            Expiry is { } e ? $"EXP {e:yyyy-MM-dd}" : null,
            Count is { } c ? $"QTY {c.ToString("0.##", CultureInfo.InvariantCulture)}" : null,
        }.Where(t => t is not null));
        if (details.Length > 0)
            canvas.Text(4, 16.5f, 3.2f, details);

        canvas.Barcode(4, 22, Symbol(), 24, Gtin is null ? 0.375f : 0.33f);
    }

    /// <summary>The symbol on the label.</summary>
    public Barcode Symbol()
    {
        if (Gtin is null)
            return Barcodes.Barcode.Code128(Sku);
        var elements = new List<(string, string)> { ("01", Gtin.PadLeft(14, '0')) };
        if (Expiry is { } expiry)
            elements.Add(("17", expiry.ToString("yyMMdd", CultureInfo.InvariantCulture)));
        if (Count is { } count)
            elements.Add(("30", count.ToString("0", CultureInfo.InvariantCulture)));
        if (LotCode is not null)
            elements.Add(("10", LotCode));   // variable length: last, so no separator is needed after it
        return Barcodes.Barcode.Gs1_128([.. elements]);
    }

    internal static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>A tote or container label: its id as a large Code 128, readable from a distance.</summary>
public sealed record ToteLabel(string ToteId) : ILabelTemplate
{
    public LabelSize Size => LabelSize.Small;

    public void Draw(ILabelCanvas canvas)
    {
        canvas.Text(4, 3, 10, ToteId, bold: true);
        canvas.Barcode(4, 17, Barcodes.Barcode.Code128(ToteId), 30, 0.5f);
    }
}

/// <summary>
/// The GS1 Logistic Label (GS1 Logistic Label Guideline): the SSCC as GS1-128 is its only mandatory element (bottom
/// block); the optional middle block carries the content GTIN, count, lot and expiry; the top block the parties.
/// </summary>
/// <param name="Sscc">The 18-digit SSCC (MintPlayer.Spark.Gs1 allocates and checks it).</param>
public sealed record SsccLabel(string Sscc, IReadOnlyList<string> From, IReadOnlyList<string> To, string? ContentGtin = null,
    decimal? Count = null, string? LotCode = null, DateOnly? Expiry = null) : ILabelTemplate
{
    public LabelSize Size => LabelSize.A6;

    public void Draw(ILabelCanvas canvas)
    {
        if (Sscc is not { Length: 18 } || !Sscc.All(char.IsAsciiDigit))
            throw new ArgumentException("An SSCC is 18 digits.", nameof(Sscc));

        canvas.Text(4, 4, 2.5f, "FROM");
        for (var i = 0; i < Math.Min(From.Count, 3); i++)
            canvas.Text(4, 8 + i * 4.5f, 3.2f, ItemLabel.Truncate(From[i], 34));
        canvas.Text(55, 4, 2.5f, "TO");
        for (var i = 0; i < Math.Min(To.Count, 4); i++)
            canvas.Text(55, 8 + i * 4.5f, 3.2f, ItemLabel.Truncate(To[i], 30), bold: i == 0);
        canvas.Fill(3, 28, 99, 0.4f);

        canvas.Text(4, 31, 3, $"SSCC {Sscc}", bold: true);
        var y = 37f;
        if (ContentGtin is not null)
        {
            canvas.Text(4, y, 3, $"CONTENT {ContentGtin.PadLeft(14, '0')}");
            y += 5;
        }
        if (Count is { } count)
        {
            canvas.Text(4, y, 3, $"COUNT {count.ToString("0", CultureInfo.InvariantCulture)}");
            y += 5;
        }
        if (LotCode is not null)
        {
            canvas.Text(4, y, 3, $"BATCH/LOT {LotCode}");
            y += 5;
        }
        if (Expiry is { } expiry)
            canvas.Text(4, y, 3, $"BEST BEFORE / USE BY {expiry:yyyy-MM-dd}");

        var middle = new List<(string, string)>();
        if (ContentGtin is not null)
            middle.Add(("02", ContentGtin.PadLeft(14, '0')));
        if (Expiry is { } e)
            middle.Add(("17", e.ToString("yyMMdd", CultureInfo.InvariantCulture)));
        if (Count is { } c)
            middle.Add(("37", c.ToString("0", CultureInfo.InvariantCulture)));
        if (LotCode is not null)
            middle.Add(("10", LotCode));
        if (middle.Count > 0)
            canvas.Barcode(4, 62, Barcode.Gs1_128([.. middle]), 30, 0.33f);

        canvas.Fill(3, 98, 99, 0.4f);
        canvas.Barcode(4, 104, Barcode.Gs1_128(("00", Sscc)), 34, 0.42f);
    }
}
