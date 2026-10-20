using System.Globalization;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;

namespace MintPlayer.Spark.ReferenceData.Sources.Australia;

/// <summary>
/// The Australian mapper (PRD §4.4): states (<c>places/au/state/ACT</c>), localities and the ACT's districts by their
/// persistent G-NAF locality PID (<c>places/au/gnaf/loc537c3f114213</c>), street localities by their PID
/// (<c>streets/au/gnaf/ACT466</c>, one per street within one locality; the street's own PID is kept as a code), and the
/// postcodes their addresses carry. G-NAF writes names in capitals; they are title-cased for display (the capitals stay
/// searchable through the folded keys).
/// </summary>
internal sealed partial class AustraliaMapper : IReferenceDataMapper
{
    private const string Gnaf = AustraliaGnafSource.SourceId;

    /// <summary>G-NAF's "no postcode" placeholder.</summary>
    private const string NoPostcode = "9999";

    public IReadOnlyList<string> Countries => ["au"];

    public IReadOnlyList<string> SourceIds => [Gnaf];

    public int Order => 10;

    public bool IsCountryAdapter => true;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(AustraliaMapper).Assembly);

    [GeneratedRegex(@"^(?<state>[A-Z]{2,3})_(?<table>STATE|LOCALITY|STREET_LOCALITY|ADDRESS_DETAIL)_psv\.psv$")]
    private static partial Regex StateTable();

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        RefAdapterKit.Country(build, "au", "AUS", "036", AustraliaProfile.Create(), Gnaf);
        var country = RefAdapterKit.CountryPlace(build, "au", Gnaf, null, "en");
        if (!inputs.TryGetValue(Gnaf, out var input))
            return Task.CompletedTask;

        IEnumerable<RefCsvRow> Table(string table) => input.Files
            .Where(f => StateTable().Match(f.Name) is { Success: true } m && m.Groups["table"].Value == table)
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .SelectMany(f => RefCsv.ReadRecords(f.Open(), '|', quotes: false));

        var streetTypes = input.Files.FirstOrDefault(f => f.Name == "Authority_Code_STREET_TYPE_AUT_psv.psv") is { } typeFile
            ? RefCsv.ReadRecords(typeFile.Open(), '|', quotes: false).ToDictionary(r => r["CODE"], r => r["NAME"], StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        // States.
        var states = new Dictionary<string, (string Id, string Abbreviation)>(StringComparer.Ordinal);
        foreach (var row in Table("STATE").Where(r => r["DATE_RETIRED"].Length == 0))
        {
            var abbreviation = row["STATE_ABBREVIATION"];
            var id = build.Ledger.Mint(Gnaf, "gnaf-state:" + row["STATE_PID"], RefIds.Place("au", "state", abbreviation));
            var place = build.Place(id);
            place.Periods = [new PlacePeriod { Kind = "au:state", Role = PlaceRole.Region, Parent = country }];
            place.Names = new TranslatedString { Entries = [RefAdapterKit.Name("en", Title(row["STATE_NAME"]), TranslatedStringKind.Official, Gnaf)] };
            place.OfficialLanguages = [new DatedLanguage { Lang = "en" }];
            place.Codes = [new PlaceCode { Scheme = "gnaf-state", Value = row["STATE_PID"] }, new PlaceCode { Scheme = "abbreviation", Value = abbreviation }];
            place.TimeZone = Zone(build, abbreviation, null, out var note);
            place.TimeZoneNote = note;
            place.Source = [Gnaf];
            states[row["STATE_PID"]] = (id, abbreviation);
        }

        // Localities (and the ACT's districts); alias-only localities are names, not units.
        var localities = new Dictionary<string, (string Id, string Name, string State)>(StringComparer.Ordinal);
        foreach (var row in Table("LOCALITY").Where(r => r["DATE_RETIRED"].Length == 0))
        {
            var cls = row["LOCALITY_CLASS_CODE"];
            if (cls == "A" || !states.TryGetValue(row["STATE_PID"], out var state)) continue;
            var pid = row["LOCALITY_PID"];
            var district = cls == "D";
            var id = build.Ledger.Mint(Gnaf, "gnaf-locality:" + pid, RefIds.Place("au", "gnaf", pid));
            var place = build.Place(id);
            var name = Title(row["LOCALITY_NAME"]);
            place.Periods = [new PlacePeriod { Kind = district ? "au:district" : "au:locality", Role = district ? PlaceRole.Subregion : PlaceRole.Municipality, Parent = state.Id }];
            place.Names = new TranslatedString { Entries = [RefAdapterKit.Name("en", name, TranslatedStringKind.Official, Gnaf)] };
            place.OfficialLanguages = [new DatedLanguage { Lang = "en" }];
            place.Codes = [new PlaceCode { Scheme = "gnaf-locality", Value = pid }, new PlaceCode { Scheme = "gnaf-class", Value = cls }];
            if (row["GNAF_LOCALITY_PID"] is { Length: > 0 } gnafId)
                place.Codes.Add(new PlaceCode { Scheme = "gnaf-locality-id", Value = gnafId });
            var primary = row["PRIMARY_POSTCODE"] is { Length: 4 } p && p != NoPostcode ? p : null;
            place.TimeZone = Zone(build, state.Abbreviation, primary, out var note);
            place.TimeZoneNote = note;
            place.Source = [Gnaf];
            localities[pid] = (id, name, state.Abbreviation);
        }

        // Street localities: one street within one locality.
        var streets = new Dictionary<string, (string Id, string Locality)>(StringComparer.Ordinal);
        foreach (var row in Table("STREET_LOCALITY").Where(r => r["DATE_RETIRED"].Length == 0))
        {
            if (!localities.TryGetValue(row["LOCALITY_PID"], out var locality)) continue;
            var pid = row["STREET_LOCALITY_PID"];
            var id = build.Ledger.Mint(Gnaf, "gnaf-street-locality:" + pid, RefIds.Street("au", "gnaf", pid));
            var street = build.Street(id, "au");
            var type = row["STREET_TYPE_CODE"];
            var name = string.Join(' ', new[] { Title(row["STREET_NAME"]), Title(type), Title(row["STREET_SUFFIX_CODE"]) }.Where(s => s.Length > 0));
            street.Names = new TranslatedString { Entries = [RefAdapterKit.Name("en", name, TranslatedStringKind.Official, Gnaf)] };
            // The abbreviated form people also write ("Allambee St") is searchable as a common name.
            if (streetTypes.TryGetValue(type, out var abbreviation) && abbreviation != type)
                street.Names.Entries.Add(RefAdapterKit.Name("en", Title(row["STREET_NAME"]) + " " + Title(abbreviation), TranslatedStringKind.Common, Gnaf));
            street.Codes = [new PlaceCode { Scheme = "gnaf-street-locality", Value = pid }];
            if (row["GNAF_STREET_PID"] is { Length: > 0 } streetPid)
                street.Codes.Add(new PlaceCode { Scheme = "gnaf-street", Value = streetPid });
            street.Type = StreetType.Streetname;
            street.Source = [Gnaf];
            RefAdapterKit.StreetIn(street, locality.Id, null, locality.Id);
            streets[pid] = (id, row["LOCALITY_PID"]);
        }

        // Postcodes from the addresses (principal, current): no house number is kept (RD5).
        var localityPostcodes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in Table("ADDRESS_DETAIL").Where(r => r["DATE_RETIRED"].Length == 0 && r["ALIAS_PRINCIPAL"] == "P"))
        {
            var postcode = row["POSTCODE"];
            if (postcode.Length != 4 || postcode == NoPostcode || !localities.TryGetValue(row["LOCALITY_PID"], out var locality)) continue;
            var postalId = build.Ledger.Mint(Gnaf, "postcode:" + postcode, RefIds.PostalCode("au", postcode));
            var postal = build.PostalCode(postalId, "au", postcode);
            RefAdapterKit.Locality(postal, locality.Name, "en", Gnaf, locality.Id, locality.Id, main: true);
            if (!localityPostcodes.TryGetValue(locality.Id, out var codes)) localityPostcodes[locality.Id] = codes = [];
            codes.Add(postcode);
            if (streets.TryGetValue(row["STREET_LOCALITY_PID"], out var street))
                RefAdapterKit.StreetIn(build.Street(street.Id, "au"), locality.Id, postalId);
        }

        // A locality without a primary postcode takes its zone exception from its addresses' postcodes.
        foreach (var (id, codes) in localityPostcodes)
            if (codes.Select(c => AustraliaProfile.PostcodeZones.GetValueOrDefault(c)).OfType<string>().Distinct().ToList() is [var zone]
                && build.Places[id].TimeZone != zone && Valid(build, zone))
                build.Places[id].TimeZone = zone;
        return Task.CompletedTask;
    }

    /// <summary>The zone of a place in <paramref name="state"/> with the postcode exceptions (Q-R10), checked against the tz database.</summary>
    private static string? Zone(RefDataBuild build, string state, string? postcode, out string? note)
    {
        note = null;
        var zone = postcode is not null && AustraliaProfile.PostcodeZones.TryGetValue(postcode, out var special)
            ? special
            : AustraliaProfile.StateZones.GetValueOrDefault(state);
        if (zone is null)
        {
            note = $"No zone is known for the G-NAF state '{state}'.";
            return null;
        }
        if (!Valid(build, zone))
        {
            note = $"{zone} is not in the tz database's zone list.";
            return null;
        }
        return zone;
    }

    private static bool Valid(RefDataBuild build, string zone) => build.KnownTimeZones.Count == 0 || build.KnownTimeZones.Contains(zone);

    /// <summary>"AUSTRALIAN CAPITAL TERRITORY" → "Australian Capital Territory"; "DRAKE-BROCKMAN" → "Drake-Brockman"; "O'CONNOR" → "O'Connor".</summary>
    internal static string Title(string upper)
    {
        if (upper.Length == 0) return upper;
        var lower = upper.ToLowerInvariant();
        var chars = lower.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (i == 0 || chars[i - 1] is ' ' or '-' or '\'' or '(')
                chars[i] = char.ToUpperInvariant(chars[i]);
        var text = new string(chars);
        // "of", "and", "the" stay lower case inside a name.
        return SmallWords().Replace(text, m => m.Value.ToLower(CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"(?<= )(Of|And|The)(?= )")]
    private static partial Regex SmallWords();
}
