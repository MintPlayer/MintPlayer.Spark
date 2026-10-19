using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;

namespace MintPlayer.Spark.ReferenceData.Sources.Global;

/// <summary>CLDR's per-locale <c>timeZoneNames.json</c> and the baseline's compact form of it (zone → locale → city).</summary>
public static class CldrExemplarCities
{
    /// <summary>Every <c>exemplarCity</c> the locale file states, as (IANA id, city). Absent = inherited from the id.</summary>
    public static IEnumerable<(string Zone, string City)> ReadLocaleFile(Stream stream, string locale)
    {
        var root = JsonNode.Parse(stream)!;
        var zones = root["main"]?[locale]?["dates"]?["timeZoneNames"]?["zone"] as JsonObject;
        if (zones is null) yield break;
        foreach (var (zone, city) in Walk(zones, string.Empty))
            yield return (zone, city);
    }

    private static IEnumerable<(string, string)> Walk(JsonObject node, string prefix)
    {
        foreach (var (key, value) in node)
        {
            if (value is not JsonObject child) continue;
            if (child["exemplarCity"] is JsonValue city && city.TryGetValue<string>(out var text))
                yield return (prefix + key, text);
            foreach (var nested in Walk(child, prefix + key + "/"))
                yield return nested;
        }
    }

    public static string Write(string version, string source, SortedDictionary<string, SortedDictionary<string, string>> zones)
        => JsonSerializer.Serialize(new { cldrVersion = version, source, zones }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
}

/// <summary>
/// The global baseline mapper (PRD §4.4, §4.13; plan R7, cut to the seed's countries): first-level units of NL, FR, LU and
/// DE from GeoNames, named from Wikidata (official languages official, others exonyms; GeoNames' own name only when
/// Wikidata has none), their ISO 3166-2 codes and per-feature time zones (Q-R10), the baseline country profiles, CLDR
/// exemplar cities as <see cref="RefTimeZone"/> documents, and the tz database's zone list for validation (RS20).
/// Runs first: country adapters (Belgium) run after it and can rely on the zone list.
/// </summary>
internal sealed partial class GlobalMapper : IReferenceDataMapper
{
    [GeneratedRegex(@"^geonames-adm1(\.cut)?\.txt$")]
    private static partial Regex GeoNamesFile();

    [GeneratedRegex(@"^wikidata-adm1-labels\.csv$")]
    private static partial Regex WikidataFile();

    [GeneratedRegex(@"^cldr-exemplar-cities(\.cut)?\.json$")]
    private static partial Regex CldrFile();

    [GeneratedRegex(@"^zone\.tab$")]
    private static partial Regex ZoneTabFile();

    public IReadOnlyList<string> Countries => [.. GlobalCountries.Codes.Select(c => c.ToLowerInvariant())];

    public IReadOnlyList<string> SourceIds => [GeoNamesSource.SourceId, WikidataSource.SourceId, CldrSource.SourceId, TzDataSource.SourceId];

    public int Order => 0;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(GlobalMapper).Assembly);

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        // tzdata first: every zone assigned below is checked against it.
        var zoneCountries = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (inputs.TryGetValue(TzDataSource.SourceId, out var tz) && tz.Find(ZoneTabFile()) is { } zoneTab)
        {
            using var reader = new StreamReader(zoneTab.Open(), Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith('#') || line.Length == 0) continue;
                var f = line.Split('\t');
                if (f.Length < 3) continue;
                build.KnownTimeZones.Add(f[2]);
                if (!zoneCountries.TryGetValue(f[2], out var list)) zoneCountries[f[2]] = list = [];
                list.Add(f[0].ToLowerInvariant());
            }
        }

        // A country with its own adapter (Belgium, a tier-1 register) gets its profile and units from it (PRD §3.2).
        foreach (var profile in BaselineProfiles.All().Where(p => !build.AdapterCountries.Contains(p.Code)))
        {
            var country = build.Country(profile.Code);
            country.Alpha3 = profile.Alpha3;
            country.Numeric = profile.Numeric;
            country.Profile = profile.Profile;
            if (!country.Source.Contains(GeoNamesSource.SourceId)) country.Source.Add(GeoNamesSource.SourceId);
        }

        var labels = new Dictionary<string, List<(string Item, string Iso, string Lang, string Label)>>(StringComparer.Ordinal);
        if (inputs.TryGetValue(WikidataSource.SourceId, out var wd) && wd.Find(WikidataFile()) is { } wdFile)
        {
            using var reader = new StreamReader(wdFile.Open(), Encoding.UTF8);
            reader.ReadLine();
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                var f = Csv(line);
                if (f.Count < 5) continue;
                if (!labels.TryGetValue(f[0], out var list)) labels[f[0]] = list = [];
                list.Add((f[1][(f[1].LastIndexOf('/') + 1)..], f[2], f[3], f[4]));
            }
        }

        if (inputs.TryGetValue(GeoNamesSource.SourceId, out var gn) && gn.Find(GeoNamesFile()) is { } gnFile)
        {
            var rows = new List<string[]>();
            using (var reader = new StreamReader(gnFile.Open(), Encoding.UTF8))
                while (reader.ReadLine() is { } line)
                    if (line.Length > 0) rows.Add(line.Split('\t'));

            // Countries first: the units' parent.
            foreach (var f in rows.Where(f => f[7] == "PCLI").OrderBy(f => f[0], StringComparer.Ordinal))
                MapFeature(build, f, labels, parent: null);
            foreach (var f in rows.Where(f => f[7] == "ADM1").OrderBy(f => f[0], StringComparer.Ordinal))
                MapFeature(build, f, labels, parent: build.Countries.GetValueOrDefault(RefIds.CountryId(f[8].ToLowerInvariant()))?.Place);
        }

        if (inputs.TryGetValue(CldrSource.SourceId, out var cldr) && cldr.Find(CldrFile()) is { } cldrFile)
        {
            var root = JsonNode.Parse(cldrFile.Open())!.AsObject();
            foreach (var (zone, byLocale) in root["zones"]!.AsObject().OrderBy(z => z.Key, StringComparer.Ordinal))
            {
                if (build.KnownTimeZones.Count > 0 && !build.KnownTimeZones.Contains(zone))
                {
                    build.Warnings.Add($"CLDR names zone {zone}, which zone.tab does not list (a link or a retired zone): skipped.");
                    continue;
                }
                var document = new RefTimeZone
                {
                    Id = RefIds.TimeZone(zone),
                    Zone = zone,
                    Countries = zoneCountries.GetValueOrDefault(zone) ?? [],
                    Source = [CldrSource.SourceId],
                };
                foreach (var (locale, city) in byLocale!.AsObject().OrderBy(l => l.Key, StringComparer.Ordinal))
                    document.Names.Entries.Add(new TranslatedStringEntry(locale, city!.GetValue<string>()) { Source = CldrSource.SourceId });
                build.TimeZones[document.Id] = document;
            }
        }
        return Task.CompletedTask;
    }

    private static void MapFeature(RefDataBuild build, string[] f, Dictionary<string, List<(string Item, string Iso, string Lang, string Label)>> labels, string? parent)
    {
        var geonameId = f[0];
        var cc = f[8].ToLowerInvariant();
        var profile = BaselineProfiles.All().FirstOrDefault(p => p.Code == cc);
        if (profile is null || build.AdapterCountries.Contains(cc)) return;
        var id = build.Ledger.Mint(GeoNamesSource.SourceId, "geonames:" + geonameId, RefIds.Place(cc, "geonames", geonameId));
        var place = build.Place(id);
        var isCountry = f[7] == "PCLI";
        place.Periods = [new PlacePeriod
        {
            Kind = isCountry ? $"{cc}:country" : profile.Profile.Levels.First(l => l.Order == 1).Key,
            Role = isCountry ? PlaceRole.Country : PlaceRole.Region,
            Parent = parent,
        }];
        var official = profile.Profile.OfficialLanguages.Select(l => l.Lang).ToList();
        place.OfficialLanguages = [.. official.Select(l => new DatedLanguage { Lang = l })];
        place.Names = new TranslatedString();
        var own = labels.GetValueOrDefault(geonameId) ?? [];
        if (isCountry)
        {
            // Country names are CLDR's (RD-DEF 10).
            foreach (var lang in official.Where(l => l is "de" or "en" or "fr" or "nl"))
                place.Names.Entries.Add(new TranslatedStringEntry(lang, CldrNames.Territory(cc, lang)) { Kind = TranslatedStringKind.Official, Source = "cldr" });
            foreach (var lang in new[] { "en", "nl", "fr", "de" }.Where(l => !official.Contains(l)))
                place.Names.Entries.Add(new TranslatedStringEntry(lang, CldrNames.Territory(cc, lang)) { Kind = TranslatedStringKind.Exonym, Source = "cldr" });
            build.Country(cc).Place = id;
        }
        else
        {
            var officialValues = new HashSet<string>(StringComparer.Ordinal);
            foreach (var lang in official)
                foreach (var label in own.Where(l => l.Lang == lang).Take(1))
                {
                    place.Names.Entries.Add(new TranslatedStringEntry(lang, label.Label) { Kind = TranslatedStringKind.Official, Source = WikidataSource.SourceId });
                    officialValues.Add(label.Label);
                }
            if (place.Names.Entries.Count == 0)
                place.Names.Entries.Add(new TranslatedStringEntry(official[0], f[1]) { Kind = TranslatedStringKind.Official, Source = GeoNamesSource.SourceId });
            foreach (var label in own.Where(l => !official.Contains(l.Lang) && !officialValues.Contains(l.Label)).GroupBy(l => l.Lang).Select(g => g.First()).OrderBy(l => l.Lang, StringComparer.Ordinal))
                place.Names.Entries.Add(new TranslatedStringEntry(label.Lang, label.Label) { Kind = TranslatedStringKind.Exonym, Source = WikidataSource.SourceId });
        }

        place.Codes = [new PlaceCode { Scheme = "geonames", Value = geonameId }];
        foreach (var iso in own.Select(l => l.Iso).Where(i => i.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(i => i, StringComparer.Ordinal))
            place.Codes.Add(new PlaceCode { Scheme = "iso3166-2", Value = iso });
        foreach (var item in own.Select(l => l.Item).Distinct(StringComparer.Ordinal).OrderBy(i => i, StringComparer.Ordinal))
            place.Codes.Add(new PlaceCode { Scheme = "wikidata", Value = item });
        if (isCountry)
            place.Codes.Add(new PlaceCode { Scheme = "iso3166", Value = cc.ToUpperInvariant() });

        place.Population = long.TryParse(f[14], NumberStyles.None, CultureInfo.InvariantCulture, out var population) && population > 0 ? population : null;
        // Q-R10: the country's zone where it has one, else the feature's own (GeoNames' per-feature time zone).
        place.TimeZone = profile.Profile.TimeZone ?? (f.Length > 17 && f[17].Length > 0 ? f[17] : null);
        if (place.TimeZone is null)
            place.TimeZoneNote = "The country has several zones and the source states none for this unit.";
        place.Source = [.. new[] { GeoNamesSource.SourceId, own.Count > 0 ? WikidataSource.SourceId : null }.OfType<string>()];
    }

    private static List<string> Csv(string line)
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
