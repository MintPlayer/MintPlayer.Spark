using System.Globalization;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Estonia;

/// <summary>
/// The Estonian mapper (PRD §4.4, plan R8). EHAK gives the current hierarchy: county (maakond) → municipality
/// (omavalitsus) → settlement unit (asustusüksus: city, city district, small borough, village). The ADS extract with
/// history gives the streets with their dated components, and with them the municipalities that no longer exist:
/// a municipality that ended when its name reappears, on the same day, as a settlement unit of the municipality that
/// took over its objects <b>continues one level down</b> (Kuressaare linn: a municipality until the 2017 reform, then a
/// city in Saaremaa vald; the PRD's identity rule); otherwise it ends unresolved (Q-R4). EHAK codes are one classifier,
/// unique across levels, so every unit is <c>places/ee/ehak/{code}</c>.
/// </summary>
internal sealed class EstoniaMapper : IReferenceDataMapper
{
    public const string Zone = "Europe/Tallinn";

    public IReadOnlyList<string> Countries => ["ee"];
    public IReadOnlyList<string> SourceIds => [EstoniaEhakSource.SourceId, EstoniaAdsSource.SourceId];
    public int Order => 10;
    public bool IsCountryAdapter => true;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(EstoniaMapper).Assembly);

    private static DateOnly? Day(string text) => Date(text, "dd.MM.yyyy", "yyyy-MM-dd");

    /// <summary>A municipality the extract names that EHAK no longer lists.</summary>
    private sealed record Gone(string Code, string Name, string County, DateOnly? End, RefCsvRow? Successor, bool Demoted);

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        var ehakRows = inputs.TryGetValue(EstoniaEhakSource.SourceId, out var ehak) && ehak.Find(EstoniaEhakSource.DbfFile()) is { } dbf
            ? DbfReader.Read(dbf.Open(), DbfReader.Baltic()).OrderBy(r => r["AKOOD"], StringComparer.Ordinal).ToList()
            : [];
        var adsRows = new List<RefCsvRow>();
        if (inputs.TryGetValue(EstoniaAdsSource.SourceId, out var ads))
            foreach (var file in ads.Files.Where(f => EstoniaAdsSource.CsvFile().IsMatch(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal))
                adsRows.AddRange(RefCsv.ReadRecords(file.Open(), ';'));

        Country(build, "ee", "EST", "233", EstoniaProfile.Create(), EstoniaEhakSource.SourceId);
        var country = CountryPlace(build, "ee", EstoniaEhakSource.SourceId, Zone);
        var countryPlace = build.Place(country);
        // The embedded CLDR subset has de/en/fr/nl only: the endonym is hand-authored like the profile.
        if (!countryPlace.Names.Entries.Any(e => e.Tag == "et"))
            countryPlace.Names.Entries.Insert(0, Name("et", "Eesti", TranslatedStringKind.Official, "profile"));
        countryPlace.OfficialLanguages = [new() { Lang = "et" }];

        // 1. Municipalities that ended, and whether each carried on as a settlement unit.
        var current = ehakRows.Select(r => r["OKOOD"]).ToHashSet(StringComparer.Ordinal);
        var gone = new List<Gone>();
        foreach (var group in adsRows.Where(r => r["TASE2_KOOD"].Length > 0 && !current.Contains(r["TASE2_KOOD"])).GroupBy(r => r["TASE2_KOOD"]).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var end = group.Select(r => Day(r["ADS_KEHTETU"])).Where(d => d is not null).Max();
            var first = group.First();
            var successor = end is { } day
                ? adsRows.FirstOrDefault(r => Day(r["ADS_KEHTIV"]) == day && r["TASE2_KOOD"] != group.Key && group.Any(g => g["ADS_OID"] == r["ADS_OID"]))
                : null;
            var demoted = successor is not null && successor["TASE3_KOOD"].Length > 0 && Key(successor["TASE3_NIMETUS"]) == Key(first["TASE2_NIMETUS"]);
            gone.Add(new Gone(group.Key, first["TASE2_NIMETUS"], first["TASE1_KOOD"], end, successor, demoted));
        }
        var demotedInto = gone.Where(g => g.Demoted).ToDictionary(g => g.Successor!["TASE3_KOOD"], g => g, StringComparer.Ordinal);

        string Unit(string code, string name, string kind, PlaceRole role, string parent, string source, DateOnly? from = null, DateOnly? to = null, string? id = null)
        {
            id ??= build.Ledger.Mint(source, "ehak:" + code, RefIds.Place("ee", "ehak", code));
            var place = build.Place(id);
            if (!place.Periods.Any(p => p.From == from && p.Kind == kind))
                place.Periods.Add(new PlacePeriod { From = from, To = to, Kind = kind, Role = role, Parent = parent });
            var previous = place.Names.Entries.FindIndex(e => e.Value == name && e.ValidTo == from && from is not null);
            if (previous >= 0)
                place.Names.Entries[previous] = place.Names.Entries[previous] with { ValidTo = to };
            else if (!place.Names.Entries.Any(e => e.Value == name && e.ValidFrom == from))
                place.Names.Entries.Add(Name("et", name, TranslatedStringKind.Official, source, from, to));
            if (!place.Codes.Any(c => c.Value == code))
                place.Codes.Add(new PlaceCode { Scheme = "ehak", Value = code, From = from, To = to });
            place.OfficialLanguages = [new() { Lang = "et" }];
            place.TimeZone = Zone;
            AddSource(place.Source, source);
            return id;
        }

        // 2. The current hierarchy. EHAK settlement types (TYYP): 6 is a city district (linnaosa); the others are
        // settlements (5 city without municipal status, 7 small borough, 8 village, …).
        var counties = new Dictionary<string, string>(StringComparer.Ordinal);
        var municipalities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in ehakRows)
        {
            counties[row["MKOOD"]] = Unit(row["MKOOD"], row["MNIMI"], "ee:county", PlaceRole.Region, country, EstoniaEhakSource.SourceId);
            municipalities[row["OKOOD"]] = Unit(row["OKOOD"], row["ONIMI"], "ee:municipality", PlaceRole.Municipality, counties[row["MKOOD"]], EstoniaEhakSource.SourceId);
        }

        // 3. The ended municipalities; a demoted one keeps its place and gains the settlement unit's code and period.
        foreach (var g in gone)
        {
            var county = counties.GetValueOrDefault(g.County) ?? build.Ledger.Find("ehak:" + g.County) ?? country;
            var id = Unit(g.Code, g.Name, "ee:municipality", PlaceRole.Municipality, county, EstoniaAdsSource.SourceId, null, g.End);
            if (g.End is not { } day) continue;
            if (g.Demoted)
            {
                var newParent = municipalities.GetValueOrDefault(g.Successor!["TASE2_KOOD"]);
                build.AddLineage(new RefLineageEvent
                {
                    Id = RefIds.LineageEvent("ee", day, LineageKind.Demote, "ehak", g.Code),
                    Country = "ee", Date = day, Kind = LineageKind.Demote,
                    Operations = [new LineageOperation { Place = id, Op = LineageOp.Demote, Into = newParent }],
                    Sources = [new LineageSource { Source = EstoniaAdsSource.SourceId, Date = day, Note = $"{g.Name} ({g.Code}) is settlement unit {g.Successor["TASE3_KOOD"]} of {g.Successor["TASE2_NIMETUS"]} from {day:yyyy-MM-dd}" }],
                });
            }
            else
            {
                build.AddLineage(new RefLineageEvent
                {
                    Id = RefIds.LineageEvent("ee", day, LineageKind.Merge, "ehak", g.Code),
                    Country = "ee", Date = day, Kind = LineageKind.Merge, Unresolved = true,
                    Operations = [new LineageOperation { Place = id, Op = LineageOp.End }],
                    Sources = [new LineageSource { Source = EstoniaAdsSource.SourceId, Date = day, Note = $"{g.Code} is not in EHAK; its successor cannot be derived without curation" }],
                });
            }
        }

        // 4. Settlement units and districts.
        foreach (var row in ehakRows)
        {
            var district = row["TYYP"] == "6";
            var kind = district ? "ee:district" : "ee:settlement";
            var role = district ? PlaceRole.Submunicipality : PlaceRole.Locality;
            if (demotedInto.TryGetValue(row["AKOOD"], out var former) && former.End is { } day)
            {
                var formerId = build.Ledger.Find("ehak:" + former.Code)!;
                build.Ledger.Alias(EstoniaEhakSource.SourceId, "ehak:" + row["AKOOD"], formerId);
                Unit(row["AKOOD"], row["ANIMI"], kind, role, municipalities[row["OKOOD"]], EstoniaEhakSource.SourceId, day, null, formerId);
                continue;
            }
            Unit(row["AKOOD"], row["ANIMI"], kind, role, municipalities[row["OKOOD"]], EstoniaEhakSource.SourceId);
        }

        // 5. Streets (TASE5, liikluspind): one per ADS object, a period per municipality state. No house numbers (RD5),
        // no postcodes (their licence is unverified, PRD §4.4).
        foreach (var street in adsRows.Where(r => r["TASE5_KOOD"].Length > 0 && r["ADS_OID"].Length > 0).GroupBy(r => r["ADS_OID"]).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var id = build.Ledger.Mint(EstoniaAdsSource.SourceId, "ads:" + street.Key, RefIds.Street("ee", "ads", street.Key));
            var document = build.Street(id, "ee");
            foreach (var row in street.OrderBy(r => DateKeys.From(Day(r["ADS_KEHTIV"]))))
            {
                if (build.Ledger.Find("ehak:" + row["TASE2_KOOD"]) is not { } municipality)
                {
                    build.Warnings.Add($"{EstoniaAdsSource.SourceId}: {street.Key} names municipality {row["TASE2_KOOD"]}, which neither EHAK nor the extract holds.");
                    continue;
                }
                var name = row["TASE5_NIMETUS_LIIGIGA"].Length > 0 ? row["TASE5_NIMETUS_LIIGIGA"] : row["TASE5_NIMETUS"];
                if (!document.Names.Entries.Any(e => e.Value == name))
                    document.Names.Entries.Add(Name("et", name, TranslatedStringKind.Official, EstoniaAdsSource.SourceId));
                var locality = row["TASE3_KOOD"].Length > 0 ? build.Ledger.Find("ehak:" + row["TASE3_KOOD"]) : null;
                StreetIn(document, municipality, null, locality, Day(row["ADS_KEHTIV"]), Day(row["ADS_KEHTETU"]));
                if (!document.Codes.Any(c => c.Value == row["TASE5_KOOD"]))
                    document.Codes.Add(new PlaceCode { Scheme = "ads-tase5", Value = row["TASE5_KOOD"] });
            }
            document.Type = StreetType.Streetname;
            AddSource(document.Source, EstoniaAdsSource.SourceId);
        }
        return Task.CompletedTask;
    }
}
