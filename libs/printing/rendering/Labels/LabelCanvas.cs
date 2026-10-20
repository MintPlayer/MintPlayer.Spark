using MintPlayer.Spark.Printing.Rendering.Barcodes;
using MintPlayer.Spark.Printing.Rendering.Pdf;
using MintPlayer.Spark.Printing.Rendering.Zpl;

namespace MintPlayer.Spark.Printing.Rendering.Labels;

/// <summary>A label's size in millimetres.</summary>
public readonly record struct LabelSize(float WidthMm, float HeightMm)
{
    /// <summary>100 × 50 mm: locations, items, cases, totes.</summary>
    public static LabelSize Small { get; } = new(100, 50);

    /// <summary>A6, 105 × 148 mm: the GS1 logistic label and parcel labels.</summary>
    public static LabelSize A6 { get; } = new(105, 148);

    /// <summary>4 × 6 in.</summary>
    public static LabelSize Label4x6 { get; } = new(101.6f, 152.4f);
}

/// <summary>
/// What a label template draws on (WX6): millimetres from the top-left. The ZPL canvas emits printer commands (native
/// barcodes, so the printer's own encoder makes the bars), the PDF canvas draws vectors. One template, two outputs, the
/// same layout.
/// </summary>
public interface ILabelCanvas
{
    LabelSize Size { get; }

    /// <summary>Text whose top is at <paramref name="y"/>, <paramref name="height"/> mm high.</summary>
    void Text(float x, float y, float height, string text, bool bold = false);

    /// <summary>A filled rectangle (a rule when thin).</summary>
    void Fill(float x, float y, float width, float height);

    /// <summary>A rectangle outline.</summary>
    void Box(float x, float y, float width, float height, float thickness);

    /// <summary>
    /// A barcode, top-left at (x, y): a linear one <paramref name="height"/> mm high with <paramref name="moduleMm"/> modules,
    /// a matrix one <paramref name="height"/> mm square. A linear symbol's human-readable line is drawn under it when asked.
    /// </summary>
    void Barcode(float x, float y, Barcode barcode, float height, float moduleMm, bool humanReadable = true);
}

/// <summary>A label: its size and how it draws itself (a template filled with its data).</summary>
public interface ILabelTemplate
{
    LabelSize Size { get; }

    void Draw(ILabelCanvas canvas);
}

/// <summary>Renders a template to ZPL (a thermal printer's resolution) or to a one-page PDF of the label's size.</summary>
public static class LabelRenderer
{
    /// <summary>ZPL II for a printer of <paramref name="dpi"/> (203 or 300).</summary>
    public static string Zpl(ILabelTemplate template, int dpi = 203)
    {
        var canvas = new ZplCanvas(template.Size, dpi);
        template.Draw(canvas);
        return canvas.Build();
    }

    /// <summary>A one-page PDF of the label's size (the print-dialog path and the agent's fallback).</summary>
    public static byte[] Pdf(ILabelTemplate template)
    {
        var canvas = new PdfCanvas(template.Size);
        template.Draw(canvas);
        return canvas.Build();
    }

    /// <summary>Several labels as one PDF, one page each (a batch printed through the dialog at once).</summary>
    public static byte[] Pdf(IReadOnlyList<ILabelTemplate> templates)
    {
        if (templates.Count == 0)
            throw new ArgumentException("No labels.", nameof(templates));
        var canvas = new PdfCanvas(templates[0].Size);
        for (var i = 0; i < templates.Count; i++)
        {
            if (i > 0)
                canvas.NewPage(templates[i].Size);
            templates[i].Draw(canvas);
        }
        return canvas.Build();
    }
}

/// <summary>The PDF canvas: millimetres to points, barcodes drawn from this package's encoders.</summary>
internal sealed class PdfCanvas : ILabelCanvas
{
    private const float Pt = PdfPageSize.PointsPerMillimetre;
    private readonly PdfWriter writer;

    public PdfCanvas(LabelSize size)
    {
        Size = size;
        writer = new PdfWriter(PdfPageSize.Millimetres(size.WidthMm, size.HeightMm));
    }

    public LabelSize Size { get; private set; }

    public void NewPage(LabelSize size)
    {
        Size = size;
        writer.NewPage(PdfPageSize.Millimetres(size.WidthMm, size.HeightMm));
    }

    public void Text(float x, float y, float height, string text, bool bold = false)
        // Helvetica's cap height is about 0.72 of its size: a size of height/0.72 fills the box; the baseline sits at its bottom.
        => writer.Text(x * Pt, (y + height) * Pt, text, height / 0.72f * Pt, bold);

    public void Fill(float x, float y, float width, float height) => writer.Rect(x * Pt, y * Pt, width * Pt, height * Pt);

    public void Box(float x, float y, float width, float height, float thickness)
        => writer.Frame((x + thickness / 2) * Pt, (y + thickness / 2) * Pt, (width - thickness) * Pt, (height - thickness) * Pt, thickness * Pt);

    public void Barcode(float x, float y, Barcode barcode, float height, float moduleMm, bool humanReadable = true)
    {
        if (barcode.IsMatrix)
        {
            writer.Modules(x * Pt, y * Pt, height * Pt, barcode.MatrixModules());
            return;
        }
        var barsHeight = humanReadable ? height - 3.5f : height;
        writer.Bars(x * Pt, y * Pt, moduleMm * Pt, barsHeight * Pt, barcode.LinearModules());
        if (humanReadable)
            Text(x, y + barsHeight + 0.8f, 2.2f, barcode.Text);
    }

    public byte[] Build() => writer.Build();
}
