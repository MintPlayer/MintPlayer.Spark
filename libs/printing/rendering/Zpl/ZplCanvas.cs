using System.Globalization;
using System.Text;
using MintPlayer.Spark.Printing.Rendering.Barcodes;
using MintPlayer.Spark.Printing.Rendering.Labels;

namespace MintPlayer.Spark.Printing.Rendering.Zpl;

/// <summary>
/// ZPL II output (Zebra ZPL II Programming Guide): <c>^XA</c> … <c>^XZ</c>, UTF-8 (<c>^CI28</c>), field origins in dots at
/// 203 or 300 dpi, scalable font 0, boxes with <c>^GB</c>, and the printer's own barcode commands: <c>^BC</c> (Code 128 and
/// GS1-128, fed this package's symbol values through invocation codes, so both outputs carry the same symbol), <c>^BE</c>
/// (EAN-13), <c>^BX</c> (Data Matrix ECC 200, quality 200) and <c>^BQ</c> (QR, model 2).
/// </summary>
public sealed class ZplCanvas : ILabelCanvas
{
    private readonly StringBuilder zpl = new();

    public ZplCanvas(LabelSize size, int dpi = 203)
    {
        if (dpi is not (203 or 300))
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "Thermal label printers here are 203 or 300 dpi.");
        Size = size;
        Dpi = dpi;
        zpl.Append(CultureInfo.InvariantCulture, $"^XA^CI28^PW{Dots(size.WidthMm)}^LL{Dots(size.HeightMm)}^LH0,0\n");
    }

    public LabelSize Size { get; }

    public int Dpi { get; }

    /// <summary>Millimetres to printer dots, rounded.</summary>
    public int Dots(float mm) => (int)Math.Round(mm * Dpi / 25.4, MidpointRounding.AwayFromZero);

    public void Text(float x, float y, float height, string text, bool bold = false)
    {
        var h = Dots(height);
        var w = bold ? (int)Math.Round(h * 1.1) : h;
        zpl.Append(CultureInfo.InvariantCulture, $"^FO{Dots(x)},{Dots(y)}^A0N,{h},{w}^FH\\^FD{EscapeField(text)}^FS\n");
    }

    public void Fill(float x, float y, float width, float height)
    {
        var w = Math.Max(1, Dots(width));
        var h = Math.Max(1, Dots(height));
        zpl.Append(CultureInfo.InvariantCulture, $"^FO{Dots(x)},{Dots(y)}^GB{w},{h},{Math.Min(w, h)}^FS\n");
    }

    public void Box(float x, float y, float width, float height, float thickness)
        => zpl.Append(CultureInfo.InvariantCulture, $"^FO{Dots(x)},{Dots(y)}^GB{Dots(width)},{Dots(height)},{Math.Max(1, Dots(thickness))}^FS\n");

    public void Barcode(float x, float y, Barcode barcode, float height, float moduleMm, bool humanReadable = true)
    {
        var module = Math.Max(1, Dots(moduleMm));
        var interpretation = humanReadable ? "Y" : "N";
        var origin = $"^FO{Dots(x)},{Dots(y)}";
        switch (barcode.Symbology)
        {
            case Symbology.Code128:
            case Symbology.Gs1_128:
            {
                var values = barcode.Symbology == Symbology.Code128 ? Code128.Encode(barcode.Text) : Code128.EncodeGs1(barcode.Elements!);
                var barsHeight = Dots(humanReadable ? height - 3.5f : height);
                zpl.Append(CultureInfo.InvariantCulture, $"{origin}^BY{module}^BCN,{barsHeight},{interpretation},N,N,N^FD{InvocationCodes(values)}^FS\n");
                break;
            }
            case Symbology.Ean13:
            {
                var barsHeight = Dots(humanReadable ? height - 3.5f : height);
                zpl.Append(CultureInfo.InvariantCulture, $"{origin}^BY{module}^BEN,{barsHeight},{interpretation},N^FD{barcode.Text[..12]}^FS\n");
                break;
            }
            case Symbology.DataMatrix:
            case Symbology.Gs1DataMatrix:
            {
                // The symbol's size follows the data; the module size makes it about `height` wide.
                var modules = barcode.MatrixModules().GetLength(0);
                var moduleDots = Math.Max(1, Dots(height) / modules);
                zpl.Append(CultureInfo.InvariantCulture, $"{origin}^BXN,{moduleDots},200,0,0,6,_^FD{DataMatrixField(barcode)}^FS\n");
                break;
            }
            case Symbology.Qr:
            {
                var modules = barcode.MatrixModules().GetLength(0);
                var magnification = Math.Clamp(Dots(height) / modules, 1, 10);
                RefuseControlPrefixes(barcode.Text, "QR");
                zpl.Append(CultureInfo.InvariantCulture, $"{origin}^BQN,2,{magnification}^FDMA,{barcode.Text}^FS\n");
                break;
            }
            default:
                throw new NotSupportedException(barcode.Symbology.ToString());
        }
    }

    /// <summary>The finished label.</summary>
    public string Build() => zpl + "^PQ1^XZ\n";

    /// <summary>
    /// <c>^BC</c> mode N data for Code 128 symbol values (start, data; the printer adds the check): <c>&gt;;</c>/<c>&gt;:</c>
    /// start C/B, <c>&gt;5</c>/<c>&gt;6</c> switch to C/B, <c>&gt;8</c> FNC1; subset C as digit pairs, subset B as the characters.
    /// </summary>
    internal static string InvocationCodes(IReadOnlyList<int> values)
    {
        var sb = new StringBuilder();
        var c = false;
        // The last two values are the check and the stop: ^BC computes and adds both.
        for (var i = 0; i < values.Count - 2; i++)
        {
            var v = values[i];
            switch (v)
            {
                case Code128.StartC: sb.Append(">;"); c = true; continue;
                case Code128.StartB: sb.Append(">:"); c = false; continue;
                case Code128.CodeC: sb.Append(">5"); c = true; continue;
                case Code128.CodeB: sb.Append(">6"); c = false; continue;
                case Code128.Fnc1: sb.Append(">8"); continue;
            }
            if (c)
                sb.Append(v.ToString("00", CultureInfo.InvariantCulture));
            else
            {
                var ch = (char)(v + 32);
                if (ch is '>' or '^' or '~')
                    throw new ArgumentException($"'{ch}' cannot be printed in a ZPL Code 128 field here (it starts a command or an invocation code).");
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    /// <summary><c>^BX</c> data with <c>_</c> as the escape: <c>_1</c> is FNC1, <c>_dNNN</c> an ASCII character by its decimal code.</summary>
    internal static string DataMatrixField(Barcode barcode)
    {
        var sb = new StringBuilder();
        void Append(string text)
        {
            foreach (var ch in text)
            {
                if (ch is '_' or '^' or '~' || ch < 32 || ch > 126)
                {
                    if (ch > 255)
                        throw new ArgumentException($"U+{(int)ch:X4} cannot be encoded in a Data Matrix field here.");
                    sb.Append(CultureInfo.InvariantCulture, $"_d{(int)ch:000}");
                }
                else
                    sb.Append(ch);
            }
        }

        if (barcode.Symbology == Symbology.Gs1DataMatrix)
        {
            sb.Append("_1");
            var elements = barcode.Elements!;
            for (var i = 0; i < elements.Count; i++)
            {
                Append(elements[i].Key + elements[i].Value);
                if (i < elements.Count - 1 && !Gs1FixedLength.IsPredefined(elements[i].Key))
                    sb.Append("_1");
            }
        }
        else
            Append(barcode.Text);
        return sb.ToString();
    }

    /// <summary>Field text under <c>^FH\</c>: the caret, the tilde and the backslash as hex escapes.</summary>
    internal static string EscapeField(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '^': sb.Append("\\5E"); break;
                case '~': sb.Append("\\7E"); break;
                case '\\': sb.Append("\\5C"); break;
                case '\r' or '\n' or '\t': sb.Append(' '); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    private static void RefuseControlPrefixes(string text, string what)
    {
        if (text.IndexOfAny(['^', '~']) >= 0)
            throw new ArgumentException($"A {what} field cannot hold '^' or '~' in ZPL here.");
    }
}
