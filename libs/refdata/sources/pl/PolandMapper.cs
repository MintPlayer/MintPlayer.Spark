using System.Text;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Poland;

/// <summary>
/// The Polish mapper (PRD §4.4, plan R8).
/// <list type="bullet">
/// <item><b>TERC</b> units: voivodeships <c>places/pl/terc/{WOJ}</c>, counties <c>places/pl/terc/{WOJ}{POW}</c>, gminy
/// <c>places/pl/terc/{WOJ}{POW}{GMI}{RODZ}</c> (kinds 1–3; the town and rural parts 4 and 5 of an urban-rural gmina are
/// not units of their own; Warsaw's districts 8 and the delegations 9 sit under their city).</item>
/// <item><b>SIMC</b> localities <c>places/pl/simc/{SYM}</c>, under their gmina, or under their parent locality
/// (<c>SYMPOD</c>) for parts of a locality.</item>
/// <item><b>ULIC</b> streets <c>streets/pl/ulic/{SYM}-{SYM_UL}</c>: <c>SYM_UL</c> is a national street-name key, not a
/// street (the fixture's "Bałtycka" 00656 exists in Sopot and in Hel), so the locality is part of the id. Names: the
/// official form with its feature ("ul. Antoniego Abrahama"), the form without it, and the surname alone.</item>
/// <item><b>Postcodes</b> from the PRG address points: per point its postcode, street and locality; flagged in the
/// credit line as not validated by Poczta Polska. No house numbers kept (RD5).</item>
/// </list>
/// Minority-language names (1,305 localities) are not in the TERYT files and are not imported.
/// </summary>
internal sealed partial class PolandMapper : IReferenceDataMapper
{
    private const string Teryt = PolandTerytSource.SourceId;
    private const string Prg = PolandPrgSource.SourceId;

    public IReadOnlyList<string> Countries => ["pl"];

    public IReadOnlyList<string> SourceIds => [Teryt, Prg];

    public int Order => 10;

    public bool IsCountryAdapter => true;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(PolandMapper).Assembly);

    [GeneratedRegex(@"^TERC_Urzedowy_\d{4}-\d{2}-\d{2}(\.cut)?\.csv$")]
    private static partial Regex TercFile();

    [GeneratedRegex(@"^SIMC_Urzedowy_\d{4}-\d{2}-\d{2}(\.cut)?\.csv$")]
    private static partial Regex SimcFile();

    [GeneratedRegex(@"^ULIC_Urzedowy_\d{4}-\d{2}-\d{2}(\.cut)?\.csv$")]
    private static partial Regex UlicFile();

    [GeneratedRegex(@"^PRG_PunktyAdresowe_[A-Z_]+(\.cut|\.zip)?\.dbf$")]
    private static partial Regex PointsFile();

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        new Run(build, inputs).Map();
        return Task.CompletedTask;
    }

    private sealed class Run(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs)
    {
        private string countryPlace = string.Empty;

        private IEnumerable<RefCsvRow> Rows(Regex pattern)
        {
            if (!inputs.TryGetValue(Teryt, out var input) || input.Find(pattern) is not { } file) yield break;
            using var stream = file.Open();
            // UTF-8 with a BOM, ';', CRLF; names hold no quotes.
            foreach (var row in RefCsv.ReadRecords(stream, ';', Encoding.UTF8, quotes: false))
                yield return row;
        }

        public void Map()
        {
            Country(build, "pl", "POL", "616", PolandProfile.Create(), Teryt);
            countryPlace = CountryPlace(build, "pl", Teryt, PolandProfile.Zone, "pl");
            MapUnits();
            MapLocalities();
            MapStreets();
            MapPostcodes();
        }

        private string Terc(string code) => build.Ledger.Mint(Teryt, "terc:" + code, RefIds.Place("pl", "terc", code));

        private string Simc(string code) => build.Ledger.Mint(Teryt, "simc:" + code, RefIds.Place("pl", "simc", code));

        /// <summary>"POMORSKIE" → "Pomorskie", "KUJAWSKO-POMORSKIE" → "Kujawsko-pomorskie" (TERC writes voivodeships in capitals).</summary>
        internal static string Voivodeship(string upper)
        {
            var lower = upper.ToLowerInvariant();
            return lower.Length == 0 ? lower : char.ToUpperInvariant(lower[0]) + lower[1..];
        }

        private void Place(string id, string kind, PlaceRole role, string? parent, string name, string scheme, string code)
        {
            var place = build.Place(id);
            place.Periods = [new PlacePeriod { Kind = kind, Role = role, Parent = parent }];
            place.Names = new TranslatedString { Entries = [Name("pl", name, TranslatedStringKind.Official, Teryt)] };
            place.OfficialLanguages = [new DatedLanguage { Lang = "pl" }];
            place.Codes = [new PlaceCode { Scheme = scheme, Value = code }];
            place.TimeZone = PolandProfile.Zone;
            place.Source = [Teryt];
        }

        private void MapUnits()
        {
            var rows = Rows(TercFile()).ToList();
            foreach (var row in rows.Where(r => r["POW"].Length == 0))
                Place(Terc(row["WOJ"]), "pl:voivodeship", PlaceRole.Region, countryPlace, Voivodeship(row["NAZWA"]), "terc", row["WOJ"]);
            foreach (var row in rows.Where(r => r["POW"].Length > 0 && r["GMI"].Length == 0))
                Place(Terc(row["WOJ"] + row["POW"]), "pl:county", PlaceRole.Subregion, Terc(row["WOJ"]), row["NAZWA"], "terc", row["WOJ"] + row["POW"]);
            foreach (var row in rows.Where(r => r["GMI"].Length > 0 && r["RODZ"] is "1" or "2" or "3"))
            {
                var code = row["WOJ"] + row["POW"] + row["GMI"] + row["RODZ"];
                Place(Terc(code), "pl:municipality", PlaceRole.Municipality, Terc(row["WOJ"] + row["POW"]), row["NAZWA"], "terc", code);
            }
            foreach (var row in rows.Where(r => r["GMI"].Length > 0 && r["RODZ"] is "8" or "9"))
            {
                // A district of Warsaw (8) or a delegation (9): under the city gmina of its county.
                var city = rows.FirstOrDefault(r => r["WOJ"] == row["WOJ"] && r["POW"] == row["POW"] && r["RODZ"] == "1");
                var code = row["WOJ"] + row["POW"] + row["GMI"] + row["RODZ"];
                Place(Terc(code), "pl:district", PlaceRole.Submunicipality,
                    city is null ? Terc(row["WOJ"] + row["POW"]) : Terc(city["WOJ"] + city["POW"] + city["GMI"] + city["RODZ"]), row["NAZWA"], "terc", code);
            }
        }

        private void MapLocalities()
        {
            foreach (var row in Rows(SimcFile()))
            {
                var sym = row["SYM"];
                var part = row["SYMPOD"].Length > 0 && row["SYMPOD"] != sym;
                var gmina = Terc(row["WOJ"] + row["POW"] + row["GMI"] + row["RODZ_GMI"]);
                Place(Simc(sym), part ? "pl:locality-part" : "pl:locality", PlaceRole.Locality, part ? Simc(row["SYMPOD"]) : gmina, row["NAZWA"], "simc", sym);
                // The locality type (RM: 96 town, 99 part of a town, 01 village, …), kept as a code.
                build.Places[Simc(sym)].Codes.Add(new PlaceCode { Scheme = "simc-rm", Value = row["RM"] });
            }
        }

        private void MapStreets()
        {
            foreach (var row in Rows(UlicFile()))
            {
                var code = $"{row["SYM"]}-{row["SYM_UL"]}";
                var street = build.Street(build.Ledger.Mint(Teryt, "ulic:" + code, RefIds.Street("pl", "ulic", code)), "pl");
                var name = string.Join(' ', new[] { row["NAZWA_2"], row["NAZWA_1"] }.Where(s => s.Length > 0));
                // CECHA "inne" means the name carries no feature ("Rynek").
                var full = row["CECHA"] is { Length: > 0 } cecha && cecha != "inne" ? $"{cecha} {name}" : name;
                street.Names = new TranslatedString { Entries = [Name("pl", full, TranslatedStringKind.Official, Teryt)] };
                if (full != name) street.Names.Entries.Add(Name("pl", name, TranslatedStringKind.Common, Teryt));
                if (row["NAZWA_2"].Length > 0) street.Names.Entries.Add(Name("pl", row["NAZWA_1"], TranslatedStringKind.Common, Teryt));
                street.Codes = [new PlaceCode { Scheme = "ulic", Value = code }, new PlaceCode { Scheme = "sym-ul", Value = row["SYM_UL"] }];
                street.Type = StreetType.Streetname;
                street.Source = [Teryt];
                StreetIn(street, Terc(row["WOJ"] + row["POW"] + row["GMI"] + row["RODZ_GMI"]), null, Simc(row["SYM"]));
            }
        }

        private void MapPostcodes()
        {
            if (!inputs.TryGetValue(Prg, out var input)) return;
            foreach (var file in input.Files.Where(f => PointsFile().IsMatch(f.Name)))
            {
                using var stream = file.Open();
                foreach (var point in PolandDbf.Read(stream, Encoding.UTF8))
                {
                    var postcode = point.GetValueOrDefault("KOD_POCZT") ?? string.Empty;
                    var gminaCode = point.GetValueOrDefault("TERYT_GMI") ?? string.Empty;
                    var simc = point.GetValueOrDefault("ID_SIMC") ?? string.Empty;
                    if (postcode.Length != 6 || gminaCode.Length != 7 || simc.Length != 7) continue;
                    var gmina = build.Ledger.Find("terc:" + gminaCode);
                    var locality = build.Ledger.Find("simc:" + simc);
                    if (gmina is null || locality is null) continue;

                    var id = build.Ledger.Mint(Prg, "postcode:" + postcode, RefIds.PostalCode("pl", postcode));
                    var postal = build.PostalCode(id, "pl", postcode);
                    var name = point.GetValueOrDefault("NAZWA_MSC") is { Length: > 0 } msc ? msc : build.Places[locality].Names.Entries[0].Value;
                    Locality(postal, name, "pl", Prg, gmina, locality, main: true);

                    var ulic = point.GetValueOrDefault("ID_ULIC") ?? string.Empty;
                    if (ulic.Length > 0 && build.Ledger.Find($"ulic:{simc}-{ulic}") is { } streetId && build.Streets.TryGetValue(streetId, out var street))
                    {
                        StreetIn(street, gmina, id, locality);
                        AddSource(street.Source, Prg);
                    }
                }
            }
        }
    }
}

/// <summary>A dBASE III reader for the PRG attribute table (UTF-8 by its <c>.cpg</c>): one record at a time, never the whole file.</summary>
internal static class PolandDbf
{
    public static IEnumerable<IReadOnlyDictionary<string, string>> Read(Stream stream, Encoding encoding)
    {
        using var reader = new BinaryReader(stream);
        var header = reader.ReadBytes(32);
        if (header.Length < 32) yield break;
        var count = BitConverter.ToUInt32(header, 4);
        var headerLength = BitConverter.ToUInt16(header, 8);
        var recordLength = BitConverter.ToUInt16(header, 10);
        var descriptors = reader.ReadBytes(headerLength - 32);
        var fields = new List<(string Name, int Offset, int Length)>();
        var offset = 1;
        for (var i = 0; i + 32 <= descriptors.Length && descriptors[i] != 0x0D; i += 32)
        {
            int length = descriptors[i + 16];
            fields.Add((Encoding.ASCII.GetString(descriptors, i, 11).TrimEnd('\0'), offset, length));
            offset += length;
        }
        for (long r = 0; r < count; r++)
        {
            var record = reader.ReadBytes(recordLength);
            if (record.Length < recordLength) yield break;
            if (record[0] == (byte)'*') continue;
            yield return fields.ToDictionary(f => f.Name, f => encoding.GetString(record, f.Offset, f.Length).Trim(), StringComparer.OrdinalIgnoreCase);
        }
    }
}
