using System.Globalization;
using System.Text;

namespace MintPlayer.Spark.Commerce.Invoicing.Pdf;

/// <summary>
/// A small, dependency-free PDF 1.4 writer (Q-O9): text in the standard Helvetica fonts (WinAnsiEncoding), lines and
/// pages. Enough for invoices, credit notes and legal documents; no images, no embedded fonts. Chosen over a library
/// because <c>libs/</c> has no PDF dependency, QuestPDF's community licence has revenue limits and headless Chromium is
/// heavy in containers (PRD §11 Q-O9). Characters outside Windows-1252 print as <c>?</c>: a document in a non-Latin
/// script needs an embedded font (a host can replace <see cref="IInvoicePdfRenderer"/>).
/// </summary>
public sealed class MinimalPdf
{
    public const float PageWidth = 595.28f;   // A4
    public const float PageHeight = 841.89f;

    private readonly List<StringBuilder> pages = [];
    private StringBuilder? current;

    public MinimalPdf() => NewPage();

    public int PageCount => pages.Count;

    public void NewPage()
    {
        current = new StringBuilder();
        pages.Add(current);
    }

    /// <summary>Writes <paramref name="text"/> with its baseline at (<paramref name="x"/>, <paramref name="y"/>) from the top-left.</summary>
    public void Text(float x, float y, string text, float size = 9, bool bold = false)
    {
        current!.Append(CultureInfo.InvariantCulture, $"BT /{(bold ? "F2" : "F1")} {N(size)} Tf {N(x)} {N(PageHeight - y)} Td (")
            .Append(Escape(text)).Append(") Tj ET\n");
    }

    /// <summary>Right-aligned text ending at <paramref name="right"/>.</summary>
    public void TextRight(float right, float y, string text, float size = 9, bool bold = false)
        => Text(right - Width(text, size, bold), y, text, size, bold);

    public void Line(float x1, float y1, float x2, float y2, float width = 0.5f)
        => current!.Append(CultureInfo.InvariantCulture, $"{N(width)} w {N(x1)} {N(PageHeight - y1)} m {N(x2)} {N(PageHeight - y2)} l S\n");

    /// <summary>Splits <paramref name="text"/> into lines no wider than <paramref name="maxWidth"/>.</summary>
    public static IEnumerable<string> Wrap(string text, float maxWidth, float size = 9, bool bold = false)
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

    /// <summary>The width of <paramref name="text"/> in points (Helvetica metrics, AFM units / 1000).</summary>
    public static float Width(string text, float size, bool bold = false)
    {
        var units = 0;
        foreach (var c in text)
            units += CharWidth(c, bold);
        return units * size / 1000f;
    }

    private static int CharWidth(char c, bool bold) => c switch
    {
        ' ' or ',' or '.' or ':' or ';' or '/' or 'i' or 'j' or 'l' or 'f' or 't' or 'I' or '!' or '|' => bold ? 333 : 278,
        '-' or '(' or ')' or 'r' => 333,
        >= '0' and <= '9' => 556,
        'm' or 'M' or 'W' => 833,
        'w' => 722,
        >= 'A' and <= 'Z' => 667,
        '€' => 556,
        _ => 556,
    };

    /// <summary>The PDF bytes.</summary>
    public byte[] Build()
    {
        using var stream = new MemoryStream();
        var offsets = new List<long>();
        void Write(string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            stream.Write(bytes);
        }
        void Object(int number, string body)
        {
            while (offsets.Count < number)
                offsets.Add(0);
            offsets[number - 1] = stream.Position;
            Write($"{number} 0 obj\n");
            Write(body);
            Write("\nendobj\n");
        }

        Write("%PDF-1.4\n");
        stream.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
        var pageObjects = Enumerable.Range(0, pages.Count).Select(i => 5 + i * 2).ToList();
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, $"<< /Type /Pages /Kids [{string.Join(" ", pageObjects.Select(p => $"{p} 0 R"))}] /Count {pages.Count} >>");
        Object(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Object(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        for (var i = 0; i < pages.Count; i++)
        {
            var content = WinAnsi(pages[i].ToString());
            Object(pageObjects[i], $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {pageObjects[i] + 1} 0 R >>");
            while (offsets.Count < pageObjects[i] + 1)
                offsets.Add(0);
            offsets[pageObjects[i]] = stream.Position;
            Write($"{pageObjects[i] + 1} 0 obj\n<< /Length {content.Length} >>\nstream\n");
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

    private static string N(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

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

    /// <summary>Encodes content as Windows-1252 (WinAnsiEncoding) without the code-pages provider.</summary>
    internal static byte[] WinAnsi(string text)
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
