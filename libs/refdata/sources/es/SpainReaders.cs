using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace MintPlayer.Spark.ReferenceData.Sources.Spain;

/// <summary>INE's municipality list (<c>{yy}codmun.xlsx</c>): one sheet per province, titled, then <c>CPRO CMUN DC NOMBRE</c>.</summary>
internal sealed record IneMunicipality(string Province, string Code, string Name);

/// <summary>One row of INE's community/province table (<c>cod_ccaa_provincia.htm</c>).</summary>
internal sealed record IneProvince(string Community, string CommunityName, string Province, string ProvinceName);

/// <summary>A singular population entity of the Callejero's <c>UP</c> file.</summary>
internal sealed record IneEntity(string Municipality, string Code, string Name);

/// <summary>A street of the Callejero's <c>VIAS</c> file (or a variation of one).</summary>
internal sealed record IneStreet(string Municipality, string Code, string Type, string Name, DateOnly? Date, char Change);

internal static partial class SpainReaders
{
    /// <summary>The Callejero's files are ISO-8859-1, fixed width, CRLF.</summary>
    public static readonly Encoding Latin1 = Encoding.Latin1;

    private static readonly XmlReaderSettings Safe = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256L * 1024 * 1024 };

    /// <summary>Reads every province sheet of the municipality workbook; rows without a 3-digit CMUN are titles.</summary>
    public static IEnumerable<IneMunicipality> Municipalities(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } sst)
        {
            using var reader = XmlReader.Create(sst.Open(), Safe);
            while (reader.ReadToFollowing("si", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"))
                shared.Add(string.Concat(XmlTexts(reader.ReadSubtree())));
        }
        var rels = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var reader = XmlReader.Create(zip.GetEntry("xl/_rels/workbook.xml.rels")!.Open(), Safe))
            while (reader.Read())
                if (reader is { NodeType: XmlNodeType.Element, LocalName: "Relationship" })
                    rels[reader.GetAttribute("Id")!] = reader.GetAttribute("Target")!;
        var sheets = new List<(string Name, string Path)>();
        using (var reader = XmlReader.Create(zip.GetEntry("xl/workbook.xml")!.Open(), Safe))
            while (reader.Read())
                if (reader is { NodeType: XmlNodeType.Element, LocalName: "sheet" })
                {
                    var rid = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships")!;
                    sheets.Add((reader.GetAttribute("name")!, "xl/" + rels[rid].TrimStart('/').Replace("xl/", string.Empty, StringComparison.Ordinal)));
                }

        foreach (var (name, path) in sheets)
        {
            if (zip.GetEntry(path) is not { } entry) continue;
            using var reader = XmlReader.Create(entry.Open(), Safe);
            while (reader.ReadToFollowing("row", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"))
            {
                var cells = new Dictionary<string, string>(StringComparer.Ordinal);
                using var row = reader.ReadSubtree();
                while (row.ReadToFollowing("c", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"))
                {
                    var column = new string([.. (row.GetAttribute("r") ?? string.Empty).TakeWhile(char.IsAsciiLetter)]);
                    var type = row.GetAttribute("t");
                    using var cell = row.ReadSubtree();
                    var text = type == "inlineStr" ? string.Concat(XmlTexts(cell)) : cell.ReadToFollowing("v", "http://schemas.openxmlformats.org/spreadsheetml/2006/main") ? cell.ReadElementContentAsString() : string.Empty;
                    if (type == "s" && int.TryParse(text, out var index) && index < shared.Count) text = shared[index];
                    cells[column] = text.Trim();
                }
                var cpro = cells.GetValueOrDefault("A") ?? string.Empty;
                var cmun = cells.GetValueOrDefault("B") ?? string.Empty;
                if (cpro.Length == 2 && cmun.Length == 3 && cpro.All(char.IsAsciiDigit) && cmun.All(char.IsAsciiDigit) && cells.GetValueOrDefault("D") is { Length: > 0 } nombre)
                    yield return new IneMunicipality(cpro, cpro + cmun, nombre);
            }
        }
    }

    private static IEnumerable<string> XmlTexts(XmlReader reader)
    {
        while (reader.Read())
            if (reader is { NodeType: XmlNodeType.Element, LocalName: "t" })
                yield return reader.ReadElementContentAsString();
    }

    [GeneratedRegex(@"<tr[^>]*>(?<row>.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TableRow();

    [GeneratedRegex(@"<td[^>]*>(?<cell>.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TableCell();

    /// <summary>The INE table of communities and provinces: an HTML page, ISO-8859-1, entities for accents. Data, never executed.</summary>
    public static IEnumerable<IneProvince> Provinces(Stream stream)
    {
        using var reader = new StreamReader(stream, Latin1);
        var html = reader.ReadToEnd();
        foreach (Match row in TableRow().Matches(html))
        {
            var cells = TableCell().Matches(row.Groups["row"].Value).Select(c => WebUtility.HtmlDecode(Regex.Replace(c.Groups["cell"].Value, "<[^>]+>", string.Empty)).Trim()).ToList();
            if (cells.Count == 4 && cells[0].Length == 2 && cells[2].Length == 2 && cells[0].All(char.IsAsciiDigit) && cells[2].All(char.IsAsciiDigit))
                yield return new IneProvince(cells[0], cells[1], cells[2], cells[3]);
        }
    }

    private static string Field(string line, int start, int end)
        => line.Length >= start ? line[(start - 1)..Math.Min(end, line.Length)].Trim() : string.Empty;

    /// <summary>
    /// The Callejero's <c>UP</c> file (design <c>Dis_nuevo.xlsx</c>, sheet UP): the singular entities (CUN ending in
    /// <c>00</c> with a singular part), named by <c>NENTSI</c> (falling back to <c>NENTSI50</c>). The municipality name
    /// sits in <c>NMUN50</c> (positions 95–144) in the full file; <c>NMUN</c> is blank there.
    /// </summary>
    public static IEnumerable<IneEntity> Entities(Stream stream)
    {
        using var reader = new StreamReader(stream, Latin1);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length < 23) continue;
            var cun = line[5..12];
            if (cun == "0000000" || !cun.EndsWith("00", StringComparison.Ordinal) || cun[2..5] == "000") continue;
            var name = Field(line, 315, 384);
            if (name.Length == 0) name = Field(line, 385, 434);
            if (name.Length == 0) continue;
            yield return new IneEntity(line[..5], cun[..5], name);
        }
    }

    /// <summary>
    /// The full <c>VIAS</c> file (design <c>Dis_nuevo.xlsx</c>, sheet VIAS): CPRO 1–2, CMUN 3–5, CVIA 6–10, FVAR 39–46,
    /// TVIA 53–57, NVIA 58–107.
    /// </summary>
    public static IEnumerable<IneStreet> Streets(Stream stream)
    {
        using var reader = new StreamReader(stream, Latin1);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length < 58) continue;
            yield return new IneStreet(line[..5], line[5..10], Field(line, 53, 57), Field(line, 58, 107), null, ' ');
        }
    }

    /// <summary>
    /// A variation file's <c>VIAS</c> (<c>Var_Callejero_*.zip</c>). Neither published design matches it; the positions
    /// were measured on <c>Var_Callejero_0126_0726</c>: old CVIA 6–10, FVAR 14–21, CVAR 26 (<c>A</c>lta, <c>B</c>aja,
    /// <c>M</c>odificación), new CVIA 27–31, TVIA 32–36, NVIA 38–87. A baja carries no name.
    /// </summary>
    public static IEnumerable<IneStreet> StreetVariations(Stream stream)
    {
        using var reader = new StreamReader(stream, Latin1);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length < 31) continue;
            var change = line[25];
            var date = RefAdapterKit.Date(Field(line, 14, 21), "yyyyMMdd");
            var code = change == 'B' ? line[5..10] : line[26..31];
            yield return new IneStreet(line[..5], code, Field(line, 32, 36), Field(line, 38, 87), date, change);
        }
    }

    /// <summary>The INE street-type table (<c>TiposVia.csv</c>, ISO-8859-1, <c>;</c>): every synonym to its description.</summary>
    public static Dictionary<string, string> StreetTypes(Stream stream)
    {
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in RefCsv.ReadRecords(stream, ';', Latin1, quotes: false))
        {
            var description = row["DESCRIPCION"];
            if (description.Length == 0) continue;
            types.TryAdd(row["SINONIMO"], description);
            types.TryAdd(row["TIPO_VIA"], description);
        }
        return types;
    }

    /// <summary>A dBASE III file (the CartoCiudad shapefile's attribute table): records as field → value, in the stated code page (UTF-8 by its .cpg).</summary>
    public static IEnumerable<IReadOnlyDictionary<string, string>> Dbf(Stream stream, Encoding encoding)
    {
        using var reader = new BinaryReader(stream);
        var header = reader.ReadBytes(32);
        if (header.Length < 32) yield break;
        var count = BitConverter.ToInt32(header, 4);
        var headerLength = BitConverter.ToInt16(header, 8);
        var recordLength = BitConverter.ToInt16(header, 10);
        var descriptors = reader.ReadBytes(headerLength - 32);
        var fields = new List<(string Name, int Offset, int Length)>();
        var offset = 1;
        for (var i = 0; i + 32 <= descriptors.Length && descriptors[i] != 0x0D; i += 32)
        {
            var name = Encoding.ASCII.GetString(descriptors, i, 11).TrimEnd('\0');
            int length = descriptors[i + 16];
            fields.Add((name, offset, length));
            offset += length;
        }
        for (var r = 0; r < count; r++)
        {
            var record = reader.ReadBytes(recordLength);
            if (record.Length < recordLength) yield break;
            if (record[0] == (byte)'*') continue;
            yield return fields.ToDictionary(f => f.Name, f => encoding.GetString(record, f.Offset, f.Length).Trim(), StringComparer.OrdinalIgnoreCase);
        }
    }
}
