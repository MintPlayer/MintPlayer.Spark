using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MintPlayer.Spark.ReferenceData.Sources.Netherlands;

/// <summary>One voorkomen (BAG state) of an object: its validity and the fields this adapter reads.</summary>
internal sealed record BagState(DateOnly? From, DateOnly? To, string Name, string Status, string? Woonplaats, string? Type, string? Postcode, string? OpenbareRuimte);

/// <summary>A gemeente–woonplaats relation (Kadaster's GEM-WPL-RELATIE): which gemeente a woonplaats lay in, when.</summary>
internal sealed record GemeenteWoonplaats(string Woonplaats, string Gemeente, DateOnly? From, DateOnly? To, string Status);

/// <summary>A CBS gemeente of the current classification (table <c>Gebieden in Nederland</c>).</summary>
internal sealed record CbsGemeente(string Code, string Name, string ProvinceCode, string ProvinceName);

/// <summary>One row of a CBS herindeling table: an ended gemeente and the gemeente it went into, effective 1 January of <see cref="Year"/>.</summary>
internal sealed record Herindeling(int Year, string OldCode, string OldName, string NewCode, string NewName, string Province);

/// <summary>
/// Streaming readers for the LVBAG extract (XML, one <c>bagObject</c> per object state), Kadaster's gemeente–woonplaats
/// relation file, the CBS OData table and the CBS herindeling pages. XML is read with DTDs prohibited and no resolver.
/// </summary>
internal static partial class NetherlandsReaders
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        MaxCharactersInDocument = 0,
    };

    /// <summary>Every element named <paramref name="localName"/>, read one at a time (the file is never loaded whole).</summary>
    private static IEnumerable<XElement> Elements(Stream stream, string localName)
    {
        using var reader = XmlReader.Create(stream, Settings);
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == localName)
            {
                if (XNode.ReadFrom(reader) is XElement element)
                    yield return element;
            }
            else if (!reader.Read())
                break;
        }
    }

    private static string? Child(XElement element, string localName)
        => element.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim();

    private static DateOnly? Day(string? text)
        => text is { Length: >= 10 } && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    /// <summary>The voorkomens of every BAG object of <paramref name="kind"/> (<c>Woonplaats</c>, <c>OpenbareRuimte</c>, <c>Nummeraanduiding</c>), by identification.</summary>
    public static IEnumerable<(string Id, BagState State)> BagObjects(Stream stream, string kind)
    {
        foreach (var bagObject in Elements(stream, "bagObject"))
        {
            var element = bagObject.Elements().FirstOrDefault(e => e.Name.LocalName == kind);
            if (element is null) continue;
            var id = element.Elements().FirstOrDefault(e => e.Name.LocalName == "identificatie")?.Value.Trim();
            if (string.IsNullOrEmpty(id)) continue;
            var woonplaats = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "WoonplaatsRef")?.Value.Trim();
            var openbareRuimte = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "OpenbareRuimteRef")?.Value.Trim();
            yield return (id, new BagState(
                Day(Child(element, "beginGeldigheid")),
                Day(Child(element, "eindGeldigheid")),
                element.Elements().FirstOrDefault(e => e.Name.LocalName == "naam")?.Value.Trim() ?? string.Empty,
                Child(element, "status") ?? string.Empty,
                woonplaats,
                element.Elements().FirstOrDefault(e => e.Name.LocalName == "type")?.Value.Trim(),
                element.Elements().FirstOrDefault(e => e.Name.LocalName == "postcode")?.Value.Trim(),
                openbareRuimte));
        }
    }

    /// <summary>Kadaster's gemeente–woonplaats relations.</summary>
    public static IEnumerable<GemeenteWoonplaats> Relations(Stream stream)
    {
        foreach (var relation in Elements(stream, "GemeenteWoonplaatsRelatie"))
        {
            string? Ref(string name) => relation.Elements().FirstOrDefault(e => e.Name.LocalName == name)?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "identificatie")?.Value.Trim();
            var woonplaats = Ref("gerelateerdeWoonplaats");
            var gemeente = Ref("gerelateerdeGemeente");
            if (woonplaats is null || gemeente is null) continue;
            yield return new GemeenteWoonplaats(woonplaats, gemeente,
                Day(Child(relation, "begindatumTijdvakGeldigheid")), Day(Child(relation, "einddatumTijdvakGeldigheid")),
                Child(relation, "status") ?? string.Empty);
        }
    }

    /// <summary>The CBS OData <c>TypedDataSet</c> (values are space-padded: trimmed here).</summary>
    public static IEnumerable<CbsGemeente> Gemeenten(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        foreach (var row in document.RootElement.GetProperty("value").EnumerateArray())
        {
            string Text(string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : string.Empty;
            var code = Text("Code_1");
            if (!code.StartsWith("GM", StringComparison.Ordinal)) continue;
            yield return new CbsGemeente(code, Text("Naam_2"), Text("Code_28"), Text("Naam_29"));
        }
    }

    [GeneratedRegex(@"<tr\b.*?</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Row();

    [GeneratedRegex(@"<t[dh]\b[^>]*>(.*?)</t[dh]>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Cell();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex Code();

    /// <summary>
    /// A CBS herindeling table ("Opgeheven gemeenten | Nieuwgevormde gemeenten | Provincie"). CBS publishes it as an
    /// HTML table per year, in two layouts (a figure table, and a table pasted from Word with empty spacer rows); only
    /// rows with a 4-digit code in the first and third cell are data. A gemeente that absorbs others is listed as both.
    /// </summary>
    public static IEnumerable<Herindeling> Herindelingen(string html, int year)
    {
        foreach (Match row in Row().Matches(html))
        {
            var cells = Cell().Matches(row.Value)
                .Select(c => Space().Replace(WebUtility.HtmlDecode(Tag().Replace(c.Groups[1].Value, " ")), " ").Trim())
                .ToList();
            if (cells.Count < 4 || !Code().IsMatch(cells[0]) || !Code().IsMatch(cells[2]))
                continue;
            yield return new Herindeling(year, cells[0], cells[1], cells[2], cells[3], cells.Count > 4 ? cells[4].TrimEnd('.') : string.Empty);
        }
    }
}
