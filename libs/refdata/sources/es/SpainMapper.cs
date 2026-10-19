using System.Text;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Spain;

/// <summary>
/// The Spanish mapper (PRD §4.4, plan R8).
/// <list type="bullet">
/// <item><b>Units</b> from INE: autonomous communities (<c>places/es/ine-ccaa/{CODAUTO}</c>), provinces
/// (<c>places/es/ine-prov/{CPRO}</c>), municipalities (<c>places/es/ine/{CPRO}{CMUN}</c>; INE keeps a code since 1970
/// and reuses an extinct one only for a re-created municipality); singular population entities from the Callejero's
/// <c>UP</c> file (<c>places/es/ine-ue/{CPRO}{CMUN}{EC}{ES}</c>).</item>
/// <item><b>Names</b>: INE's slash-joined official names split into the co-official language and Spanish
/// ("Donostia/San Sebastián" → eu Donostia, es San Sebastián); inverted articles restored ("Coruña, A" → "A Coruña");
/// a single name is official in every official language of the place. Callejero names are upper case: title-cased
/// (generated).</item>
/// <item><b>Streets</b> from <c>VIAS</c> (<c>streets/es/ine/{CPRO}{CMUN}-{CVIA}</c>), named "{type} {name}" with INE's
/// type table, unless the name already holds its type ("Kale Nagusia"). The variation files give dates: an alta
/// starts a street, a baja ends it, a modification dates its current name.</item>
/// <item><b>Postcodes</b> from CartoCiudad, never from the Callejero's <c>TRAM</c> (possibly Correos-derived).</item>
/// <item><b>Zones</b> per place (Q-R10): the Canaries, Ceuta and Melilla have their own (<see cref="SpainProfile.ZoneOf"/>).</item>
/// </list>
/// </summary>
internal sealed partial class SpainMapper : IReferenceDataMapper
{
    public IReadOnlyList<string> Countries => ["es"];

    public IReadOnlyList<string> SourceIds => [SpainIneCodesSource.SourceId, SpainCallejeroSource.SourceId, SpainCallejeroVariationsSource.SourceId, SpainCartoCiudadSource.SourceId];

    public int Order => 10;

    public bool IsCountryAdapter => true;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(SpainMapper).Assembly);

    [GeneratedRegex(@"^\d{2}codmun(\.cut)?\.xlsx$")]
    internal static partial Regex MunicipalityFile();

    [GeneratedRegex(@"^cod_ccaa_provincia(\.cut)?\.htm$")]
    internal static partial Regex ProvinceFile();

    [GeneratedRegex(@"^UP\.D\d{6}\.G\d{6}(\.cut)?$")]
    internal static partial Regex EntityFile();

    [GeneratedRegex(@"^VIAS\.D\d{6}\.G\d{6}(\.cut)?$")]
    internal static partial Regex StreetFile();

    [GeneratedRegex(@"^VIAS\.D\d{6}\.A\d{6}(\.cut)?$")]
    internal static partial Regex VariationFile();

    [GeneratedRegex(@"^TiposVia\.csv$")]
    internal static partial Regex StreetTypeFile();

    [GeneratedRegex(@"\.dbf$", RegexOptions.IgnoreCase)]
    internal static partial Regex DbfFile();

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        new Run(build, inputs).Map();
        return Task.CompletedTask;
    }

    private sealed class Run(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs)
    {
        private const string Ine = SpainIneCodesSource.SourceId;
        private const string Callejero = SpainCallejeroSource.SourceId;
        private const string Variations = SpainCallejeroVariationsSource.SourceId;
        private const string Carto = SpainCartoCiudadSource.SourceId;

        private string countryPlace = string.Empty;

        /// <summary>CPRO → (community code, province place id).</summary>
        private readonly Dictionary<string, (string Community, string Id)> provinces = new(StringComparer.Ordinal);

        /// <summary>INE municipality code → place id.</summary>
        private readonly Dictionary<string, string> municipalities = new(StringComparer.Ordinal);

        private SourceFile? File(string source, Regex pattern) => inputs.TryGetValue(source, out var input) ? input.Find(pattern) : null;

        public void Map()
        {
            Country(build, "es", "ESP", "724", SpainProfile.Create(), Ine);
            countryPlace = CountryPlace(build, "es", Ine, null, "es");
            MapProvinces();
            MapMunicipalities();
            MapEntities();
            MapStreets();
            MapPostcodes();
        }

        private List<string> Languages(string community)
            => SpainProfile.CoOfficialLanguage(community) is { } co ? [co, "es"] : ["es"];

        /// <summary>Official names: a slash-joined name split per language, a single one stated in every official language.</summary>
        private static TranslatedString Names(string name, List<string> languages, string source)
        {
            var parts = SpainNames.Split(name);
            var names = new TranslatedString();
            if (parts.Count > 1 && languages.Count > 1)
            {
                // INE writes the co-official form first and the Spanish one second ("Araba/Álava", "Donostia/San Sebastián").
                names.Entries.Add(Name(languages[0], parts[0], TranslatedStringKind.Official, source));
                names.Entries.Add(Name("es", parts[^1], TranslatedStringKind.Official, source));
            }
            else
            {
                var value = parts.Count > 0 ? parts[0] : name.Trim();
                foreach (var lang in languages)
                    names.Entries.Add(Name(lang, value, TranslatedStringKind.Official, source));
            }
            return names;
        }

        private void MapProvinces()
        {
            if (File(Ine, ProvinceFile()) is not { } file) return;
            using var stream = file.Open();
            foreach (var row in SpainReaders.Provinces(stream).OrderBy(r => r.Province, StringComparer.Ordinal))
            {
                var languages = Languages(row.Community);
                var community = build.Ledger.Mint(Ine, "ine-ccaa:" + row.Community, RefIds.Place("es", "ine-ccaa", row.Community));
                var place = build.Place(community);
                if (place.Periods.Count == 0)
                {
                    place.Periods = [new PlacePeriod { Kind = "es:community", Role = PlaceRole.Region, Parent = countryPlace }];
                    place.Names = Names(row.CommunityName, languages, Ine);
                    place.OfficialLanguages = [.. languages.Select(l => new DatedLanguage { Lang = l })];
                    place.Codes = [new PlaceCode { Scheme = "ine-ccaa", Value = row.Community }];
                    place.TimeZone = SpainProfile.ZoneOf(row.Province);
                    place.Source = [Ine];
                }

                var id = build.Ledger.Mint(Ine, "ine-prov:" + row.Province, RefIds.Place("es", "ine-prov", row.Province));
                var province = build.Place(id);
                province.Periods = [new PlacePeriod { Kind = "es:province", Role = PlaceRole.Subregion, Parent = community }];
                province.Names = Names(row.ProvinceName, languages, Ine);
                province.OfficialLanguages = [.. languages.Select(l => new DatedLanguage { Lang = l })];
                province.Codes = [new PlaceCode { Scheme = "ine-prov", Value = row.Province }];
                province.TimeZone = SpainProfile.ZoneOf(row.Province);
                province.Source = [Ine];
                provinces[row.Province] = (row.Community, id);
            }
        }

        private void MapMunicipalities()
        {
            if (File(Ine, MunicipalityFile()) is not { } file) return;
            using var stream = file.Open();
            foreach (var row in SpainReaders.Municipalities(stream).OrderBy(r => r.Code, StringComparer.Ordinal))
            {
                if (!provinces.TryGetValue(row.Province, out var province))
                {
                    build.Warnings.Add($"es-ine: municipality {row.Code} lies in province {row.Province}, which the community table does not list.");
                    continue;
                }
                var languages = Languages(province.Community);
                var id = build.Ledger.Mint(Ine, "ine:" + row.Code, RefIds.Place("es", "ine", row.Code));
                var place = build.Place(id);
                place.Periods = [new PlacePeriod { Kind = "es:municipality", Role = PlaceRole.Municipality, Parent = province.Id }];
                place.Names = Names(row.Name, languages, Ine);
                place.OfficialLanguages = [.. languages.Select(l => new DatedLanguage { Lang = l })];
                place.Codes = [new PlaceCode { Scheme = "ine", Value = row.Code }];
                place.TimeZone = SpainProfile.ZoneOf(row.Province);
                place.Source = [Ine];
                municipalities[row.Code] = id;
            }
        }

        private void MapEntities()
        {
            if (File(Callejero, EntityFile()) is not { } file) return;
            using var stream = file.Open();
            foreach (var entity in SpainReaders.Entities(stream))
            {
                if (!municipalities.TryGetValue(entity.Municipality, out var municipality)) continue;
                var code = entity.Municipality + entity.Code;
                var id = build.Ledger.Mint(Callejero, "ine-ue:" + code, RefIds.Place("es", "ine-ue", code));
                var place = build.Place(id);
                if (place.Periods.Count > 0) continue;
                var parent = build.Places[municipality];
                place.Periods = [new PlacePeriod { Kind = "es:entity", Role = PlaceRole.Locality, Parent = municipality }];
                place.Names = new TranslatedString { Entries = [Name("es", SpainNames.TitleCase(entity.Name), TranslatedStringKind.Official, Callejero)] };
                place.OfficialLanguages = [.. parent.OfficialLanguages.Select(l => new DatedLanguage { Lang = l.Lang })];
                place.Codes = [new PlaceCode { Scheme = "ine-ue", Value = code }];
                place.TimeZone = parent.TimeZone;
                place.Source = [Callejero];
            }
        }

        private void MapStreets()
        {
            var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File(Callejero, StreetTypeFile()) is { } typeFile)
                using (var stream = typeFile.Open())
                    types = SpainReaders.StreetTypes(stream);

            if (File(Callejero, StreetFile()) is { } file)
                using (var stream = file.Open())
                    foreach (var row in SpainReaders.Streets(stream))
                        Street(row, types, Callejero);

            if (File(Variations, VariationFile()) is not { } variations) return;
            using var changes = variations.Open();
            foreach (var change in SpainReaders.StreetVariations(changes).OrderBy(c => c.Date))
            {
                if (!municipalities.ContainsKey(change.Municipality)) continue;
                var id = build.Ledger.Find($"ine-via:{change.Municipality}-{change.Code}");
                switch (change.Change)
                {
                    case 'A':
                        var street = Street(change, types, Variations);
                        if (street is not null)
                            foreach (var period in street.Periods) period.From = change.Date;
                        break;
                    case 'B' when id is not null && build.Streets.TryGetValue(id, out var ended):
                        foreach (var period in ended.Periods) period.To = change.Date;
                        AddSource(ended.Source, Variations);
                        break;
                    case 'B':
                        build.Warnings.Add($"es-callejero-var: baja of street {change.Municipality}-{change.Code} on {change.Date:yyyy-MM-dd}, which the current Callejero no longer lists: not imported.");
                        break;
                    case 'M' when id is not null && build.Streets.TryGetValue(id, out var renamed):
                        // The current name holds since the modification; the earlier one is not in the files.
                        renamed.Names.Entries = [.. renamed.Names.Entries.Select(e => e.Kind == TranslatedStringKind.Official ? e with { ValidFrom = change.Date } : e)];
                        AddSource(renamed.Source, Variations);
                        break;
                }
            }
        }

        private RefStreet? Street(IneStreet row, Dictionary<string, string> types, string source)
        {
            if (!municipalities.TryGetValue(row.Municipality, out var municipality) || row.Name.Length == 0) return null;
            var code = $"{row.Municipality}-{row.Code}";
            var id = build.Ledger.Mint(source, "ine-via:" + code, RefIds.Street("es", "ine", code));
            var street = build.Street(id, "es");
            if (street.Names.Entries.Count == 0)
            {
                var name = SpainNames.TitleCase(row.Name);
                var type = types.TryGetValue(row.Type, out var description) ? SpainNames.TitleCase(description) : SpainNames.TitleCase(row.Type);
                var full = name.Split(' ').Contains(type, StringComparer.OrdinalIgnoreCase) || row.Name.Split(' ').Contains(row.Type, StringComparer.OrdinalIgnoreCase)
                    ? name : $"{type} {name}";
                var place = build.Places[municipality];
                // Street names are written in the municipality's language as INE lists them: tagged with its first official language.
                street.Names.Entries.Add(Name(place.OfficialLanguages.FirstOrDefault()?.Lang ?? "es", full, TranslatedStringKind.Official, source));
                if (full != name)
                    street.Names.Entries.Add(Name(place.OfficialLanguages.FirstOrDefault()?.Lang ?? "es", name, TranslatedStringKind.Common, source));
                street.Codes = [new PlaceCode { Scheme = "ine-via", Value = code }];
                street.Type = StreetType.Streetname;
                StreetIn(street, municipality, null);
            }
            AddSource(street.Source, source);
            return street;
        }

        private void MapPostcodes()
        {
            if (!inputs.TryGetValue(Carto, out var input)) return;
            foreach (var file in input.Files.Where(f => DbfFile().IsMatch(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                using var stream = file.Open();
                foreach (var row in SpainReaders.Dbf(stream, Encoding.UTF8))
                {
                    var code = row.GetValueOrDefault("COD_POSTAL") ?? string.Empty;
                    var ine = row.GetValueOrDefault("INE_MUN") ?? string.Empty;
                    if (code.Length != 5 || !municipalities.TryGetValue(ine, out var municipality)) continue;
                    var id = build.Ledger.Mint(Carto, "postcode:" + code, RefIds.PostalCode("es", code));
                    var postal = build.PostalCode(id, "es", code);
                    var place = build.Places[municipality];
                    var name = place.Names.Entries.FirstOrDefault(e => e.Tag == "es")?.Value ?? place.Names.Entries[0].Value;
                    Locality(postal, name, "es", Carto, municipality, municipality, main: true);
                    // A street's postcodes would need CartoCiudad's portal layer (street id per address point): not linked yet.
                }
            }
        }
    }
}
