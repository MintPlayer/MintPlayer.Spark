using System.Globalization;
using System.Text;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Pdf;

/// <summary>
/// A small, dependency-free PDF 1.4 writer for tickets: Helvetica text (WinAnsiEncoding), filled rectangles in RGB
/// (the QR modules, the template's colour bar) and JPEG images (the template's logo and background, DCTDecode, no
/// re-encoding). Same approach as Invoicing's writer (PRD §11 Q-O9: no PDF dependency in <c>libs/</c>), kept separate so
/// Tickets does not depend on Invoicing (§4.1). Characters outside Windows-1252 print as <c>?</c>.
/// </summary>
public sealed class TicketPdfWriter
{
    public const float PageWidth = 595.28f;   // A4
    public const float PageHeight = 841.89f;

    private readonly List<StringBuilder> pages = [];
    private readonly List<(string Name, byte[] Jpeg, int Width, int Height, int Components)> images = [];
    private StringBuilder? current;

    public TicketPdfWriter() => NewPage();

    public int PageCount => pages.Count;

    public void NewPage()
    {
        current = new StringBuilder();
        pages.Add(current);
    }

    /// <summary>Text with its baseline at (<paramref name="x"/>, <paramref name="y"/>) from the top-left, in <paramref name="color"/> (<c>#rrggbb</c>).</summary>
    public void Text(float x, float y, string text, float size = 10, bool bold = false, string? color = null)
    {
        var (r, g, b) = Rgb(color ?? "#111827");
        current!.Append(CultureInfo.InvariantCulture, $"BT {N(r)} {N(g)} {N(b)} rg /{(bold ? "F2" : "F1")} {N(size)} Tf {N(x)} {N(PageHeight - y)} Td (")
            .Append(Escape(text)).Append(") Tj ET\n");
    }

    /// <summary>A filled rectangle with its top-left at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void Rect(float x, float y, float width, float height, string color)
    {
        var (r, g, b) = Rgb(color);
        current!.Append(CultureInfo.InvariantCulture, $"{N(r)} {N(g)} {N(b)} rg {N(x)} {N(PageHeight - y - height)} {N(width)} {N(height)} re f\n");
    }

    public void Line(float x1, float y1, float x2, float y2, float width = 0.5f, string color = "#9ca3af")
    {
        var (r, g, b) = Rgb(color);
        current!.Append(CultureInfo.InvariantCulture, $"{N(r)} {N(g)} {N(b)} RG {N(width)} w {N(x1)} {N(PageHeight - y1)} m {N(x2)} {N(PageHeight - y2)} l S\n");
    }

    /// <summary>
    /// Draws a QR module matrix (<c>true</c> = dark) as filled squares, <paramref name="size"/> points wide, top-left at
    /// (<paramref name="x"/>, <paramref name="y"/>), on a white background.
    /// </summary>
    public void Modules(float x, float y, float size, bool[,] modules)
    {
        var count = modules.GetLength(0);
        var cell = size / count;
        Rect(x, y, size, size, "#ffffff");
        var sb = current!;
        sb.Append("0 0 0 rg\n");
        for (var row = 0; row < count; row++)
        {
            // One rectangle per run of dark modules in a row keeps the content stream small.
            var col = 0;
            while (col < count)
            {
                if (!modules[row, col]) { col++; continue; }
                var start = col;
                while (col < count && modules[row, col]) col++;
                sb.Append(CultureInfo.InvariantCulture, $"{N(x + start * cell)} {N(PageHeight - y - (row + 1) * cell)} {N((col - start) * cell)} {N(cell)} re\n");
            }
        }
        sb.Append("f\n");
    }

    /// <summary>
    /// Draws a JPEG scaled into the box (keeping its ratio), top-left at (<paramref name="x"/>, <paramref name="y"/>).
    /// Returns false (and draws nothing) when <paramref name="jpeg"/> is not a baseline or progressive JPEG it can size.
    /// </summary>
    public bool Jpeg(float x, float y, float maxWidth, float maxHeight, byte[] jpeg)
    {
        if (!TryReadJpegSize(jpeg, out var width, out var height, out var components) || components is not (1 or 3))
            return false;
        var name = "Im" + (images.Count + 1).ToString(CultureInfo.InvariantCulture);
        images.Add((name, jpeg, width, height, components));
        var scale = Math.Min(maxWidth / width, maxHeight / height);
        var w = width * scale;
        var h = height * scale;
        current!.Append(CultureInfo.InvariantCulture, $"q {N(w)} 0 0 {N(h)} {N(x)} {N(PageHeight - y - h)} cm /{name} Do Q\n");
        return true;
    }

    /// <summary>Splits <paramref name="text"/> into lines no wider than <paramref name="maxWidth"/>.</summary>
    public static IEnumerable<string> Wrap(string text, float maxWidth, float size = 10, bool bold = false)
    {
        foreach (var paragraph in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (Width(candidate, size, bold) > maxWidth && line.Length > 0)
                {
                    yield return line.ToString();
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(candidate);
                }
            }
            yield return line.ToString();
        }
    }

    /// <summary>The width of <paramref name="text"/> in points (Helvetica metrics, approximate).</summary>
    public static float Width(string text, float size, bool bold = false)
    {
        var units = 0;
        foreach (var c in text)
            units += c switch
            {
                ' ' or ',' or '.' or ':' or ';' or '/' or 'i' or 'j' or 'l' or 'f' or 't' or 'I' or '!' or '|' => bold ? 333 : 278,
                '-' or '(' or ')' or 'r' => 333,
                >= '0' and <= '9' => 556,
                'm' or 'M' or 'W' => 833,
                'w' => 722,
                >= 'A' and <= 'Z' => 667,
                _ => 556,
            };
        return units * size / 1000f;
    }

    /// <summary>The PDF bytes.</summary>
    public byte[] Build()
    {
        using var stream = new MemoryStream();
        var offsets = new List<long>();
        void Write(string s) => stream.Write(Encoding.ASCII.GetBytes(s));
        void Begin(int number)
        {
            while (offsets.Count < number)
                offsets.Add(0);
            offsets[number - 1] = stream.Position;
            Write($"{number} 0 obj\n");
        }

        Write("%PDF-1.4\n");
        stream.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
        // 1 catalog, 2 pages, 3-4 fonts, then the images, then page + content pairs.
        var firstImage = 5;
        var firstPage = firstImage + images.Count;
        var pageObjects = Enumerable.Range(0, pages.Count).Select(i => firstPage + i * 2).ToList();
        Begin(1); Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Begin(2); Write($"<< /Type /Pages /Kids [{string.Join(" ", pageObjects.Select(p => $"{p} 0 R"))}] /Count {pages.Count} >>\nendobj\n");
        Begin(3); Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        Begin(4); Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");
        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            Begin(firstImage + i);
            var space = image.Components == 1 ? "/DeviceGray" : "/DeviceRGB";
            Write($"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace {space} /BitsPerComponent 8 /Filter /DCTDecode /Length {image.Jpeg.Length} >>\nstream\n");
            stream.Write(image.Jpeg);
            Write("\nendstream\nendobj\n");
        }
        var xobjects = images.Count == 0 ? "" : " /XObject << " + string.Join(" ", images.Select((im, i) => $"/{im.Name} {firstImage + i} 0 R")) + " >>";
        for (var i = 0; i < pages.Count; i++)
        {
            var content = WinAnsi(pages[i].ToString());
            Begin(pageObjects[i]);
            Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >>{xobjects} >> /Contents {pageObjects[i] + 1} 0 R >>\nendobj\n");
            Begin(pageObjects[i] + 1);
            Write($"<< /Length {content.Length} >>\nstream\n");
            stream.Write(content);
            Write("\nendstream\nendobj\n");
        }
        var xref = stream.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return stream.ToArray();
    }

    /// <summary>Reads a JPEG's size from its SOF marker (baseline or progressive). Pure.</summary>
    public static bool TryReadJpegSize(byte[] jpeg, out int width, out int height, out int components)
    {
        width = height = components = 0;
        if (jpeg is not { Length: > 4 } || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return false;
        var i = 2;
        while (i + 9 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF) { i++; continue; }
            var marker = jpeg[i + 1];
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                height = (jpeg[i + 5] << 8) | jpeg[i + 6];
                width = (jpeg[i + 7] << 8) | jpeg[i + 8];
                components = jpeg[i + 9];
                return width > 0 && height > 0;
            }
            if (marker == 0xD9 || length < 2)
                return false;
            i += 2 + length;
        }
        return false;
    }

    /// <summary>Whether <paramref name="color"/> is <c>#rrggbb</c>.</summary>
    public static bool IsColor(string? color)
        => color is { Length: 7 } && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit);

    private static (float R, float G, float B) Rgb(string color)
    {
        if (!IsColor(color))
            return (0, 0, 0);
        var value = int.Parse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (((value >> 16) & 0xFF) / 255f, ((value >> 8) & 0xFF) / 255f, (value & 0xFF) / 255f);
    }

    private static string N(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '(': sb.Append("\\("); break;
                case ')': sb.Append("\\)"); break;
                case '\n' or '\r' or '\t': sb.Append(' '); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static byte[] WinAnsi(string text)
    {
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            bytes[i] = c switch
            {
                '€' => 0x80, '‚' => 0x82, '„' => 0x84, '…' => 0x85, '‘' => 0x91, '’' => 0x92, '“' => 0x93, '”' => 0x94,
                '•' => 0x95, '–' => 0x96, '—' => 0x97, '™' => 0x99, 'Š' => 0x8A, 'š' => 0x9A, 'Œ' => 0x8C, 'œ' => 0x9C,
                'Ž' => 0x8E, 'ž' => 0x9E, 'Ÿ' => 0x9F,
                < (char)0x80 => (byte)c,
                >= (char)0xA0 and <= (char)0xFF => (byte)c,
                _ => (byte)'?',
            };
        }
        return bytes;
    }
}
