using System.Text;
using System.Xml;
using System.Xml.Linq;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;

namespace MintPlayer.Spark.ReferenceData.Sources.Czechia;

/// <summary>
/// The Czech mapper (PRD §4.4): RÚIAN units (kraj → okres → obec → část obce) from the VFR territorial structure, then
/// streets and PSČ from the address-place CSVs. Ids are RÚIAN codes (<c>places/cz/obec/563889</c>,
/// <c>streets/cz/ulice/252000</c>); periods start at the unit's <c>DatumVzniku</c> and end at its <c>DatumZaniku</c> or
/// <c>PlatiDo</c> when the file states one. RÚIAN publishes no successor links: an obec that ends is recorded as
/// unresolved lineage (Q-R4), never guessed.
/// </summary>
internal sealed class CzechiaMapper : IReferenceDataMapper
{
    private const string Vf = "urn:cz:isvs:ruian:schemas:VymennyFormatTypy:v1";

    static CzechiaMapper() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public IReadOnlyList<string> Countries => ["cz"];

    public IReadOnlyList<string> SourceIds => [CzechiaUnitsSource.SourceId, CzechiaAddressesSource.SourceId];

    public int Order => 10;

    public bool IsCountryAdapter => true;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(CzechiaMapper).Assembly);

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        RefAdapterKit.Country(build, "cz", "CZE", "203", CzechiaProfile.Create(), CzechiaUnitsSource.SourceId);
        var country = RefAdapterKit.CountryPlace(build, "cz", CzechiaUnitsSource.SourceId, CzechiaProfile.Zone, "cs");

        if (inputs.TryGetValue(CzechiaUnitsSource.SourceId, out var units))
            foreach (var file in units.Files.Where(f => CzechiaUnitsSource.UnitsFile().IsMatch(f.Name)))
                MapUnits(build, file, country);

        if (inputs.TryGetValue(CzechiaAddressesSource.SourceId, out var addresses))
            foreach (var file in addresses.Files.Where(f => CzechiaAddressesSource.AddressFile().IsMatch(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal))
                MapAddresses(build, file);
        return Task.CompletedTask;
    }

    // ---- territorial structure (VFR XML) ----

    private static readonly Dictionary<string, (string Scheme, string Kind, PlaceRole Role, string? ParentTag)> Units = new(StringComparer.Ordinal)
    {
        ["Stat"] = ("ruian-stat", "cz:country", PlaceRole.Country, null),
        ["Vusc"] = ("vusc", "cz:region", PlaceRole.Region, null),
        ["Okres"] = ("okres", "cz:district", PlaceRole.Subregion, "Vusc"),
        ["Obec"] = ("obec", "cz:municipality", PlaceRole.Municipality, "Okres"),
        ["CastObce"] = ("cobce", "cz:part", PlaceRole.Submunicipality, "Obec"),
    };

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = true,
    };

    private static void MapUnits(RefDataBuild build, SourceFile file, string country)
    {
        using var stream = file.Open();
        using var reader = XmlReader.Create(stream, Settings);
        reader.MoveToContent();
        while (!reader.EOF)
        {
            // A unit carries gml:id; its container of the same name (<vf:Vusc> around <vf:Vusc gml:id=…>) does not.
            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == Vf && Units.ContainsKey(reader.LocalName) && reader.HasAttributes)
            {
                var element = (XElement)XNode.ReadFrom(reader);
                MapUnit(build, element, country);
            }
            else
                reader.Read();
        }
    }

    private static void MapUnit(RefDataBuild build, XElement element, string country)
    {
        var (scheme, kind, role, parentTag) = Units[element.Name.LocalName];
        var code = Child(element, "Kod");
        var name = Child(element, "Nazev");
        if (code is null || name is null)
        {
            build.Warnings.Add($"{CzechiaUnitsSource.SourceId}: a {element.Name.LocalName} without Kod or Nazev was skipped.");
            return;
        }
        var from = RefAdapterKit.Date(Child(element, "DatumVzniku"));
        var to = RefAdapterKit.OpenEnd(RefAdapterKit.Date(Child(element, "DatumZaniku")) ?? RefAdapterKit.Date(Child(element, "PlatiDo")));

        if (role == PlaceRole.Country)
        {
            var state = build.Place(country);
            state.Codes.Add(new PlaceCode { Scheme = scheme, Value = code });
            if (Child(element, "NutsLau") is { } nuts) state.Codes.Add(new PlaceCode { Scheme = "nuts", Value = nuts });
            RefAdapterKit.AddSource(state.Source, CzechiaUnitsSource.SourceId);
            return;
        }

        var parent = parentTag is null
            ? country
            : element.Elements().FirstOrDefault(e => e.Name.LocalName == parentTag) is { } link && Child(link, "Kod") is { } parentCode
                ? Id(build, Units[parentTag].Scheme, parentCode)
                : null;
        if (parent is null)
            build.Warnings.Add($"{CzechiaUnitsSource.SourceId}: {element.Name.LocalName} {code} names no {parentTag}.");

        var id = Id(build, scheme, code);
        var place = build.Place(id);
        place.Periods = [new PlacePeriod { From = from, To = to, Kind = kind, Role = role, Parent = parent }];
        place.Names = new TranslatedString { Entries = [RefAdapterKit.Name("cs", name, TranslatedStringKind.Official, CzechiaUnitsSource.SourceId, from, to)] };
        place.OfficialLanguages = [new DatedLanguage { Lang = "cs" }];
        place.Codes = [new PlaceCode { Scheme = scheme, Value = code, From = from, To = to }];
        if (Child(element, "NutsLau") is { } lau)
            place.Codes.Add(new PlaceCode { Scheme = "nuts", Value = lau });
        place.TimeZone = CzechiaProfile.Zone;
        place.Source = [CzechiaUnitsSource.SourceId];

        // RÚIAN states the end of a unit but never its successor: unresolved, flagged, never guessed (Q-R4).
        if (to is { } end && role == PlaceRole.Municipality)
        {
            place.LineageUnresolved = true;
            build.AddLineage(new RefLineageEvent
            {
                Id = RefIds.LineageEvent("cz", end, LineageKind.Absorb, scheme, code),
                Country = "cz",
                Date = end,
                Kind = LineageKind.Absorb,
                Unresolved = true,
                Operations = [new LineageOperation { Place = id, Op = LineageOp.End }],
                Sources = [new LineageSource { Source = CzechiaUnitsSource.SourceId, Date = end, Note = $"obec {code} ends; RÚIAN names no successor" }],
            });
        }
    }

    private static string Id(RefDataBuild build, string scheme, string code)
        => build.Ledger.Mint(CzechiaUnitsSource.SourceId, $"{scheme}:{code}", RefIds.Place("cz", scheme, code));

    private static string? Child(XElement element, string localName)
        => element.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim() is { Length: > 0 } value ? value : null;

    // ---- address places (CSV per obec): streets and PSČ, no house numbers ----

    private static void MapAddresses(RefDataBuild build, SourceFile file)
    {
        using var stream = file.Open();
        foreach (var row in RefCsv.ReadRecords(stream, ';', Encoding.GetEncoding(1250), quotes: false))
        {
            var obec = row["Kód obce"];
            var psc = row["PSČ"];
            if (obec.Length == 0 || psc.Length == 0)
                continue;
            var municipality = Id(build, "obec", obec);
            var part = row["Kód části obce"] is { Length: > 0 } partCode ? Id(build, "cobce", partCode) : null;

            var postalId = build.Ledger.Mint(CzechiaAddressesSource.SourceId, "psc:" + psc, RefIds.PostalCode("cz", psc));
            var postal = build.PostalCode(postalId, "cz", psc);
            // The city line names the obec (RÚIAN does not carry the delivery post's own name).
            RefAdapterKit.Locality(postal, row["Název obce"], "cs", CzechiaAddressesSource.SourceId, municipality, municipality, main: true);

            if (row["Kód ulice"] is not { Length: > 0 } streetCode)
                continue;
            var streetId = build.Ledger.Mint(CzechiaAddressesSource.SourceId, "ulice:" + streetCode, RefIds.Street("cz", "ulice", streetCode));
            var street = build.Street(streetId, "cz");
            if (street.Names.Entries.Count == 0)
            {
                street.Names = new TranslatedString { Entries = [RefAdapterKit.Name("cs", row["Název ulice"], TranslatedStringKind.Official, CzechiaAddressesSource.SourceId)] };
                street.Codes = [new PlaceCode { Scheme = "ruian-ulice", Value = streetCode }];
                street.Type = StreetType.Streetname;
                street.Source = [CzechiaAddressesSource.SourceId];
            }
            RefAdapterKit.StreetIn(street, municipality, postalId, part);
        }
    }
}
