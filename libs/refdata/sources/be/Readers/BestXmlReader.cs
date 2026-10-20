using System.Globalization;
using System.Xml;

namespace MintPlayer.Spark.ReferenceData.Sources.Belgium.Readers;

/// <summary>One object of a BeST full-download file (municipality, postal info, part of municipality, street name).</summary>
/// <param name="Region">BeST's source: <c>flanders</c>, <c>brussels</c> or <c>wallonia</c>.</param>
/// <param name="Namespace">The object's namespace as published (three spellings exist; see <see cref="BestNamespace"/>).</param>
/// <param name="Names">(language, spelling) in file order.</param>
/// <param name="Status">Street names only: <c>current</c>, <c>retired</c>, <c>proposed</c>, <c>reserved</c>, <c>rejected</c>.</param>
/// <param name="Type">Street names only: <c>streetname</c> or <c>hamlet</c>.</param>
/// <param name="Municipality">Street names only: the assigning municipality's NIS code.</param>
public sealed record BestRecord(
    string Kind, string Region, string Namespace, string ObjectId, string? Version,
    IReadOnlyList<(string Lang, string Value)> Names,
    string? Status, DateOnly? ValidFrom, DateOnly? ValidTo, string? Type, string? HomonymAddition, string? Municipality);

/// <summary>
/// Streams a BeST full-download XML file (<c>{Region}{Kind}{yyyyMMdd}.xml</c>, CC BY 4.0) with an <see cref="XmlReader"/>
/// (DTD prohibited, no resolver, a character cap). Brussels files start with a BOM; the reader handles it.
/// </summary>
public static class BestXmlReader
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 512L * 1024 * 1024,
        IgnoreWhitespace = true,
        IgnoreComments = true,
    };

    private static readonly HashSet<string> Objects = ["municipality", "postalInfo", "partOfMunicipality", "streetName"];

    public static IEnumerable<BestRecord> Read(Stream stream)
    {
        using var reader = XmlReader.Create(stream, Settings);
        var region = string.Empty;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName == "source" && reader.Depth == 1)
            {
                region = reader.ReadElementContentAsString().Trim();
                continue;
            }
            if (reader.Depth == 1 && Objects.Contains(reader.LocalName))
            {
                using var subtree = reader.ReadSubtree();
                yield return ReadObject(subtree, reader.LocalName, region);
            }
        }
    }

    private static BestRecord ReadObject(XmlReader reader, string kind, string region)
    {
        string ns = string.Empty, id = string.Empty;
        string? version = null, status = null, type = null, homonym = null, municipality = null, lang = null;
        DateOnly? from = null, to = null;
        var names = new List<(string, string)>();
        var path = new Stack<string>();
        reader.Read();                       // the object element itself
        var more = reader.Read();
        while (more && !reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (path.Count > 0) path.Pop();
                more = reader.Read();
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element || reader.IsEmptyElement)
            {
                more = reader.Read();
                continue;
            }
            var parent = path.Count > 0 ? path.Peek() : kind;
            var name = reader.LocalName;
            // ReadElementContentAsString leaves the reader on the next node: no Read() after it.
            switch (name)
            {
                case "namespace" when parent == "code":
                    ns = reader.ReadElementContentAsString().Trim(); continue;
                case "objectIdentifier" when parent == "code":
                    id = reader.ReadElementContentAsString().Trim(); continue;
                case "versionIdentifier" when parent == "code":
                    version = reader.ReadElementContentAsString().Trim(); continue;
                case "objectIdentifier" when parent == "isAssignedByMunicipality":
                    municipality = reader.ReadElementContentAsString().Trim(); continue;
                case "language" when parent == "name":
                    lang = reader.ReadElementContentAsString().Trim(); continue;
                case "spelling" when parent == "name":
                    names.Add((lang ?? string.Empty, reader.ReadElementContentAsString().Trim())); continue;
                case "status" when parent == "streetNameStatus":
                    status = reader.ReadElementContentAsString().Trim(); continue;
                case "validFrom" when parent == "streetNameStatus":
                    from = Day(reader.ReadElementContentAsString()); continue;
                case "validTo" when parent == "streetNameStatus":
                    to = Day(reader.ReadElementContentAsString()); continue;
                case "type" when parent == kind:
                    type = reader.ReadElementContentAsString().Trim(); continue;
                case "homonymAddition":
                    homonym = reader.ReadElementContentAsString().Trim(); continue;
            }
            path.Push(name);
            more = reader.Read();
        }
        if (id.Length == 0)
            throw new InvalidDataException($"BeST {region} {kind}: an object without an objectIdentifier.");
        return new BestRecord(kind, region, ns, id, version, names, status, from, to, type, homonym, municipality);
    }

    /// <summary>The calendar date of a register timestamp (UTC date part as published; a register date, never a legal one).</summary>
    private static DateOnly? Day(string text)
        => text.Trim().Length >= 10 && DateOnly.TryParseExact(text.Trim()[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
}

/// <summary>
/// BeST, the Flemish API and the 2025 diff files spell one namespace three ways (scheme or none, <c>www.</c>, a
/// trailing slash, case: §4.3). Normalized, a street namespace names its region, the prefix of its id.
/// </summary>
public static class BestNamespace
{
    public static string Normalize(string ns)
    {
        var value = ns.Trim().ToLowerInvariant();
        foreach (var scheme in new[] { "https://", "http://" })
            if (value.StartsWith(scheme, StringComparison.Ordinal)) value = value[scheme.Length..];
        if (value.StartsWith("www.", StringComparison.Ordinal)) value = value[4..];
        return value.TrimEnd('/');
    }

    /// <summary><c>vlg</c>, <c>bru</c> or <c>wal</c> for a street-name namespace; <see langword="null"/> for anything else.</summary>
    public static string? StreetRegion(string ns) => Normalize(ns) switch
    {
        "data.vlaanderen.be/id/straatnaam" => "vlg",
        "databrussels.be/id/streetname" => "bru",
        "geodata.wallonie.be/id/streetname" => "wal",
        _ => null,
    };

    /// <summary>The street-id region of a BeST source name (<c>flanders</c> → <c>vlg</c>).</summary>
    public static string? Region(string source) => source.Trim().ToLowerInvariant() switch
    {
        "flanders" => "vlg",
        "brussels" => "bru",
        "wallonia" => "wal",
        _ => null,
    };
}
