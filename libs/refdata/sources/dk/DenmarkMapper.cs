using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Denmark;

/// <summary>
/// The Danish mapper (PRD §4.4, plan R8). DAGI gives regions and municipalities, DAR the named roads with their
/// municipality parts and postcodes. Both registers are <b>bitemporal</b>: only the current registration of each object
/// is read (<c>registreringTil</c> empty), and its effective time (<c>virkningFra</c>/<c>virkningTil</c>) becomes the
/// period. Objects with status 3 (in force) and 4 (retired) are kept; proposals are skipped. Ids are the registers'
/// UUIDs (<c>id_lokalId</c>) for roads, the kommunekode, regionskode and postnr for the others. Greenland and the
/// Faroe Islands are not part of the data (they have their own ISO codes).
/// </summary>
internal sealed class DenmarkMapper : IReferenceDataMapper
{
    public const string Zone = "Europe/Copenhagen";

    public IReadOnlyList<string> Countries => ["dk"];
    public IReadOnlyList<string> SourceIds => [DenmarkDagiSource.SourceId, DenmarkDarSource.SourceId];
    public int Order => 10;
    public bool IsCountryAdapter => true;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(DenmarkMapper).Assembly);

    /// <summary>The current registration of an object: no registration end, status in force (3) or retired (4).</summary>
    private static bool Current(JsonElement item)
        => Text(item, "registreringTil") is null && Text(item, "status") is "3" or "4";

    private static string? Text(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static (DateOnly? From, DateOnly? To) Effective(JsonElement item) => (Date(Text(item, "virkningFra")), OpenEnd(Date(Text(item, "virkningTil"))));

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        Country(build, "dk", "DNK", "208", DenmarkProfile.Create(), DenmarkDagiSource.SourceId);
        var country = CountryPlace(build, "dk", DenmarkDagiSource.SourceId, Zone);
        var countryPlace = build.Place(country);
        // The embedded CLDR subset has de/en/fr/nl only: the endonym is hand-authored like the profile.
        if (!countryPlace.Names.Entries.Any(e => e.Tag == "da"))
            countryPlace.Names.Entries.Insert(0, Name("da", "Danmark", TranslatedStringKind.Official, "profile"));
        countryPlace.OfficialLanguages = [new() { Lang = "da" }];

        var municipalities = new Dictionary<string, string>(StringComparer.Ordinal);
        if (inputs.TryGetValue(DenmarkDagiSource.SourceId, out var dagi))
        {
            var regions = new Dictionary<string, string>(StringComparer.Ordinal);
            var items = dagi.Files.OrderBy(f => f.Name, StringComparer.Ordinal).SelectMany(f => Read(f)).Where(x => Current(x.Item)).ToList();
            foreach (var (_, item) in items.Where(x => x.Array == "RegionsinddelingList").OrderBy(x => Text(x.Item, "regionskode"), StringComparer.Ordinal))
                regions[Text(item, "regionskode")!] = Unit(build, item, "regionskode", "dk:region", PlaceRole.Region, country);
            foreach (var (_, item) in items.Where(x => x.Array == "KommuneinddelingList").OrderBy(x => Text(x.Item, "kommunekode"), StringComparer.Ordinal))
            {
                var parent = regions.GetValueOrDefault(Text(item, "regionskode") ?? string.Empty) ?? country;
                municipalities[Text(item, "kommunekode")!] = Unit(build, item, "kommunekode", "dk:municipality", PlaceRole.Municipality, parent);
            }
        }

        if (inputs.TryGetValue(DenmarkDarSource.SourceId, out var dar))
        {
            var postcodes = new Dictionary<string, string>(StringComparer.Ordinal); // DAR uuid → postal-code id
            var roads = new Dictionary<string, RefStreet>(StringComparer.Ordinal);
            var roadPostcodes = new List<(string Road, string Postcode)>();
            var roadParts = new List<(string Road, string Municipality, string Code, DateOnly? From, DateOnly? To)>();
            foreach (var (array, item) in dar.Files.OrderBy(f => f.Name, StringComparer.Ordinal).SelectMany(f => Read(f)).Where(x => Current(x.Item)))
            {
                switch (array)
                {
                    case "PostnummerList" when Text(item, "postnr") is { } postnr:
                        var postalId = build.Ledger.Mint(DenmarkDarSource.SourceId, "postnr:" + postnr, RefIds.PostalCode("dk", postnr));
                        var postal = build.PostalCode(postalId, "dk", postnr);
                        var locality = Locality(postal, Text(item, "navn") ?? postnr, "da", DenmarkDarSource.SourceId, null, main: true);
                        postcodes[Text(item, "id_lokalId")!] = postalId;
                        break;
                    case "NavngivenVejList" when Text(item, "id_lokalId") is { } uuid:
                        var streetId = build.Ledger.Mint(DenmarkDarSource.SourceId, "dar-vej:" + uuid, RefIds.Street("dk", "dar", uuid));
                        var street = build.Street(streetId, "dk");
                        var (from, to) = Effective(item);
                        street.Names.Entries = [Name("da", Text(item, "vejnavn") ?? string.Empty, TranslatedStringKind.Official, DenmarkDarSource.SourceId, from, to)];
                        street.Type = StreetType.Streetname;
                        AddSource(street.Source, DenmarkDarSource.SourceId);
                        roads[uuid] = street;
                        break;
                    case "NavngivenVejKommunedelList" when Text(item, "navngivenVej") is { } road && Text(item, "kommune") is { } kommune:
                        var (partFrom, partTo) = Effective(item);
                        roadParts.Add((road, kommune, Text(item, "vejkode") ?? string.Empty, partFrom, partTo));
                        break;
                    case "NavngivenVejPostnummerList" when Text(item, "navngivenVej") is { } r && Text(item, "postnummer") is { } p:
                        roadPostcodes.Add((r, p));
                        break;
                }
            }

            // A road in several municipalities (Vesterbrogade: København and Frederiksberg) gets a period in each.
            foreach (var part in roadParts.OrderBy(p => p.Road, StringComparer.Ordinal).ThenBy(p => p.Municipality, StringComparer.Ordinal))
            {
                if (!roads.TryGetValue(part.Road, out var street)) continue;
                if (!municipalities.TryGetValue(part.Municipality, out var municipality))
                {
                    build.Warnings.Add($"{DenmarkDarSource.SourceId}: road {part.Road} names municipality {part.Municipality}, which DAGI does not hold.");
                    continue;
                }
                var period = StreetIn(street, municipality, null, null, part.From, part.To);
                street.Codes.Add(new PlaceCode { Scheme = "vejkode", Value = $"{part.Municipality}-{part.Code}", From = part.From, To = part.To });
                foreach (var (_, postcode) in roadPostcodes.Where(rp => rp.Road == part.Road))
                    if (postcodes.TryGetValue(postcode, out var postalId))
                    {
                        if (!period.PostalCodes.Contains(postalId)) period.PostalCodes.Add(postalId);
                        // The postcode's locality lies in the municipalities of the roads that use it.
                        var locality = build.PostalCodes[postalId].Localities[0];
                        if (!locality.Municipalities.Any(m => m.Id == municipality))
                            locality.Municipalities.Add(new DatedLink { Id = municipality });
                    }
            }
        }
        return Task.CompletedTask;
    }

    private static IEnumerable<(string Array, JsonElement Item)> Read(SourceFile file)
    {
        using var stream = file.Open();
        foreach (var item in JsonArrayStream.Read(stream))
            yield return item;
    }

    private static string Unit(RefDataBuild build, JsonElement item, string codeField, string kind, PlaceRole role, string parent)
    {
        var code = Text(item, codeField)!;
        var scheme = codeField == "kommunekode" ? "kommune" : "region";
        var id = build.Ledger.Mint(DenmarkDagiSource.SourceId, $"{scheme}:{code}", RefIds.Place("dk", scheme, code));
        var place = build.Place(id);
        var (from, to) = Effective(item);
        place.Periods = [new PlacePeriod { From = from, To = to, Kind = kind, Role = role, Parent = parent }];
        place.Names.Entries = [Name("da", Text(item, "navn") ?? code, TranslatedStringKind.Official, DenmarkDagiSource.SourceId, from, to)];
        place.Codes = [new PlaceCode { Scheme = scheme, Value = code, From = from, To = to }, new PlaceCode { Scheme = "dagi", Value = Text(item, "id_lokalId") ?? code }];
        place.OfficialLanguages = [new() { Lang = "da" }];
        place.TimeZone = Zone;
        AddSource(place.Source, DenmarkDagiSource.SourceId);
        return id;
    }
}
