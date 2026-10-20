using System.IO.Compression;
using System.Text;
using System.Xml;

namespace MintPlayer.Spark.ReferenceData.Sources.Austria;

/// <summary>
/// A streaming reader for OpenDocument spreadsheets (<c>.ods</c>, Statistik Austria's change file): the sheets of
/// <c>content.xml</c> as rows of cell texts. Downloads are data: the XML is read with DTDs prohibited and no resolver.
/// Repeated empty cells and rows (<c>number-columns-repeated="16378"</c>) are capped, and trailing empty cells dropped.
/// </summary>
internal static class OdsReader
{
    private const string Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const int MaxRepeat = 64;

    public static Dictionary<string, List<string[]>> Read(Stream ods)
    {
        using var zip = new ZipArchive(ods, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry("content.xml") ?? throw new InvalidDataException("The spreadsheet has no content.xml.");
        if (entry.Length > 256L * 1024 * 1024)
            throw new InvalidDataException("The spreadsheet's content.xml is larger than the 256 MB cap.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = false,
        });

        var sheets = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
        List<string[]>? rows = null;
        List<string>? row = null;
        StringBuilder? cell = null;
        var cellRepeat = 1;
        var rowRepeat = 1;
        var paragraphs = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                var empty = reader.IsEmptyElement;
                if (reader.NamespaceURI == Table && reader.LocalName == "table")
                {
                    rows = [];
                    sheets[reader.GetAttribute("name", Table) ?? $"Sheet{sheets.Count + 1}"] = rows;
                }
                else if (reader.NamespaceURI == Table && reader.LocalName == "table-row")
                {
                    row = [];
                    rowRepeat = Repeat(reader.GetAttribute("number-rows-repeated", Table));
                    if (empty) EndRow(rows, ref row, rowRepeat);
                }
                else if (reader.NamespaceURI == Table && reader.LocalName is "table-cell" or "covered-table-cell")
                {
                    cell = new StringBuilder();
                    paragraphs = 0;
                    cellRepeat = Repeat(reader.GetAttribute("number-columns-repeated", Table));
                    if (empty) EndCell(row, ref cell, cellRepeat);
                }
                else if (cell is not null && reader.NamespaceURI == Text)
                {
                    switch (reader.LocalName)
                    {
                        case "p" when paragraphs++ > 0:
                        case "line-break":
                            cell.Append('\n');
                            break;
                        case "tab":
                            cell.Append('\t');
                            break;
                        case "s":
                            cell.Append(' ', Math.Min(Repeat(reader.GetAttribute("c", Text)), MaxRepeat));
                            break;
                    }
                }
            }
            else if (cell is not null && reader.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace)
            {
                cell.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.NamespaceURI == Table)
            {
                if (reader.LocalName is "table-cell" or "covered-table-cell")
                    EndCell(row, ref cell, cellRepeat);
                else if (reader.LocalName == "table-row")
                    EndRow(rows, ref row, rowRepeat);
                else if (reader.LocalName == "table")
                    rows = null;
            }
        }
        return sheets;
    }

    private static int Repeat(string? value) => int.TryParse(value, out var n) && n > 0 ? n : 1;

    private static void EndCell(List<string>? row, ref StringBuilder? cell, int repeat)
    {
        if (row is not null && cell is not null)
        {
            var text = cell.ToString();
            for (var i = 0; i < Math.Min(repeat, MaxRepeat); i++)
                row.Add(text);
        }
        cell = null;
    }

    private static void EndRow(List<string[]>? rows, ref List<string>? row, int repeat)
    {
        if (rows is not null && row is not null)
        {
            while (row.Count > 0 && row[^1].Length == 0)
                row.RemoveAt(row.Count - 1);
            if (row.Count > 0)
                for (var i = 0; i < Math.Min(repeat, MaxRepeat); i++)
                    rows.Add([.. row]);
        }
        row = null;
    }
}
