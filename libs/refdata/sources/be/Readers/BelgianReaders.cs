using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace MintPlayer.Spark.ReferenceData.Sources.Belgium.Readers;

/// <summary>One row of Statbel's REFNIS file: a unit's code, level, parent and names over one validity window.</summary>
public sealed record RefnisRow(int Level, string Code, string? Parent, string NameDe, string NameFr, string NameNl, DateOnly From, DateOnly? To);

/// <summary>
/// Statbel REFNIS (<c>TU_COM_REFNIS-yyyyMMdd.txt</c>, CC BY 4.0): <b>cp1252</b>, pipe-separated, CRLF, a header line,
/// dates <c>dd/MM/yyyy</c> with <c>31/12/9999</c> as "open"; <c>-</c> for no parent (Brussels has no province). The
/// end date is inclusive, so <see cref="RefnisRow.To"/> is the day after it (half-open periods). Streams line by line.
/// </summary>
public static class RefnisReader
{
    static RefnisReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static IEnumerable<RefnisRow> Read(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.GetEncoding(1252), detectEncodingFromByteOrderMarks: false);
        var header = reader.ReadLine() ?? throw new InvalidDataException("REFNIS: empty file.");
        if (!header.StartsWith("LVL_REFNIS|CD_REFNIS|", StringComparison.Ordinal))
            throw new InvalidDataException($"REFNIS: unexpected header '{header}'.");
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            var f = line.Split('|');
            if (f.Length < 8)
                throw new InvalidDataException($"REFNIS: a line with {f.Length} fields: '{line}'.");
            var end = Date(f[7]);
            yield return new RefnisRow(
                int.Parse(f[0], CultureInfo.InvariantCulture), f[1], f[2] is "-" or "" ? null : f[2],
                f[3].Trim(), f[4].Trim(), f[5].Trim(),
                Date(f[6]), end.Year == 9999 ? null : end.AddDays(1));
        }
    }

    private static DateOnly Date(string text) => DateOnly.ParseExact(text.Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture);
}

/// <summary>One row of Statbel's NIS6 list: a section (deelgemeente) with its current municipality.</summary>
public sealed record Nis6Row(string Municipality, string MunicipalityNl, string MunicipalityFr, string MunicipalityDe, string Code, string NameNl, string NameFr);

/// <summary>
/// Statbel NIS6 (<c>NIS6withnamefrom01012025.xlsx</c>): an Office Open XML workbook; the first sheet holds
/// <c>CNIS5_2025 | T_MUN_NL | T_MUN_FR | T_MUN_DE | C_NIS6 | T_NIS6_NL | T_NIS6_FR</c>. Read with a streaming
/// <see cref="XmlReader"/> (DTD prohibited, no resolver) over the shared strings and the sheet: no spreadsheet library.
/// Section codes change between editions (45017A → 45017Z) and combined parts ("MOREGEM + OOIKE") occur.
/// </summary>
public static class Nis6Reader
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 64L * 1024 * 1024,
        IgnoreWhitespace = true,
    };

    public static IEnumerable<Nis6Row> Read(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var strings = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } shared)
        {
            using var reader = XmlReader.Create(shared.Open(), Settings);
            var current = new StringBuilder();
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si") current.Clear();
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t") current.Append(reader.ReadElementContentAsString());
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "si") strings.Add(current.ToString());
            }
        }

        var sheet = zip.GetEntry("xl/worksheets/sheet1.xml") ?? throw new InvalidDataException("NIS6: the workbook has no first sheet.");
        using var sheetReader = XmlReader.Create(sheet.Open(), Settings);
        var header = true;
        var cells = new List<string>();
        string? type = null;
        while (sheetReader.Read())
        {
            if (sheetReader.NodeType == XmlNodeType.Element && sheetReader.LocalName == "row")
            {
                cells.Clear();
                if (sheetReader.IsEmptyElement) continue;
            }
            else if (sheetReader.NodeType == XmlNodeType.Element && sheetReader.LocalName == "c")
                type = sheetReader.GetAttribute("t");
            else if (sheetReader.NodeType == XmlNodeType.Element && sheetReader.LocalName is "v" or "t")
            {
                var value = sheetReader.ReadElementContentAsString();
                cells.Add(type == "s" ? strings[int.Parse(value, CultureInfo.InvariantCulture)] : value);
            }
            else if (sheetReader.NodeType == XmlNodeType.EndElement && sheetReader.LocalName == "row")
            {
                if (header)
                {
                    header = false;
                    if (cells.Count < 7 || !cells[0].StartsWith("CNIS5", StringComparison.Ordinal) || cells[4] != "C_NIS6")
                        throw new InvalidDataException($"NIS6: unexpected header '{string.Join('|', cells)}'.");
                    continue;
                }
                if (cells.Count >= 7)
                    yield return new Nis6Row(cells[0].Trim(), cells[1].Trim(), cells[2].Trim(), cells[3].Trim(), cells[4].Trim(), cells[5].Trim(), cells[6].Trim());
            }
        }
    }
}

/// <summary>One row of BeST postal streets: a (postcode, street) pair with the municipality and, for Wallonia, the part of municipality.</summary>
public sealed record PostalStreetRow(
    string Region, string PostalCode, string PostalNl, string PostalFr, string PostalDe,
    string StreetNl, string StreetFr, string StreetDe, string CityNl, string CityFr, string CityDe,
    string CityPartNl, string CityPartFr, string CityPartDe,
    string StreetNamespace, string StreetId, string CityNamespace, string CityId, string CityPartNamespace, string CityPartId);

/// <summary>
/// BeST postal streets (<c>postalstreets-latest.zip</c>, FPS BOSA, CC BY 4.0): one CSV per region, UTF-8 (Brussels with a
/// BOM), every field quoted except Brussels' and Flanders' empty citypart columns. Flanders' postal names are
/// slash-joined with the main municipality in capitals; Brussels and Wallonia carry none (Wallonia names the part of
/// municipality instead). Namespaces are spelled three ways (scheme, <c>www.</c>, case) and are normalized by the mapper.
/// </summary>
public static class PostalStreetsReader
{
    private static readonly string[] Expected =
    [
        "postal_id", "postal_nl", "postal_fr", "postal_de", "street_nl", "street_fr", "street_de", "city_nl", "city_fr", "city_de",
        "citypart_nl", "citypart_fr", "citypart_de", "street_prefix", "street_no", "street_version", "city_prefix", "city_no",
        "city_version", "citypart_prefix", "citypart_no", "citypart_version",
    ];

    public static IEnumerable<PostalStreetRow> Read(Stream stream, string region)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var header = Split(reader.ReadLine() ?? throw new InvalidDataException("Postal streets: empty file."));
        if (!header.SequenceEqual(Expected))
            throw new InvalidDataException($"Postal streets: unexpected header '{string.Join(',', header)}'.");
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            var f = Split(line);
            if (f.Count != Expected.Length)
                throw new InvalidDataException($"Postal streets: a line with {f.Count} fields.");
            yield return new PostalStreetRow(region, f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12],
                f[13], f[14], f[16], f[17], f[19], f[20]);
        }
    }

    /// <summary>RFC 4180 fields of one line: quoted or bare, <c>""</c> inside quotes is a quote.</summary>
    internal static List<string> Split(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else if (c != '\r') current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }
}
