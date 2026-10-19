using System.Globalization;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Sources.Belgium.Readers;

namespace MintPlayer.Spark.ReferenceData.Sources.Belgium;

/// <summary>
/// Belgium in depth (PRD §4.4, plan R6). Maps REFNIS units, NIS6 sections and BeST postal streets, and derives lineage
/// <b>without curation</b> (RD4):
/// <list type="bullet">
/// <item><b>Merge</b> (Kruisem 2019, Hasselt 2025 per Q-R1, Beveren-Kruibeke-Zwijndrecht 2025, Bastogne 2024): a code
/// starting on day D whose NIS6 sections carry the prefixes of codes that ended the day before; the predecessors end
/// (<see cref="LineageOp.End"/>, whole) and their sections re-parent. Dates are Statbel's (Q-R3: the primary source).</item>
/// <item><b>Recode</b> (La Louvière 55022 → 58001, 2019): one code ends, one starts the same day, the same names, no
/// other participant: the same place gets a new dated code (and a new parent).</item>
/// <item><b>Absorb + demote</b> (Borsbeek 2025; the 1977 fusion, e.g. Ooike into Wortegem-Petegem): an ended
/// municipality that continues as one section of a continuing municipality keeps its identity one level down. For
/// 1977 the match is the section's name within the ended municipality's arrondissement (RS22). An ended code that
/// matches nothing, or more than one section, is recorded unresolved (Q-R4), never guessed.</item>
/// <item><b>Rename</b> (Basse-Sambre → Sambreville, 1979): the same code with new names: dated names.</item>
/// </list>
/// Hierarchy comes from REFNIS periods, never from code digits; a NIS6 prefix is evidence only for the period before a
/// merger. Ooike's 1977 cession of its east part to Oudenaarde is not machine-readable (PRD §12) and is not derived.
/// </summary>
internal sealed partial class BelgiumMapper : IReferenceDataMapper
{
    private static readonly DateOnly Fusion1977 = new(1977, 1, 1);
    private static readonly DateOnly RefnisStart = new(1970, 1, 1);

    /// <summary>German-speaking municipalities (official language de); their REFNIS codes.</summary>
    private static readonly HashSet<string> GermanSpeaking = ["63001", "63012", "63013", "63023", "63040", "63048", "63061", "63067", "63087"];

    [GeneratedRegex(@"^(Brussels|Flanders|Wallonia)_postal_street(\.cut)?\.csv$")]
    internal static partial Regex PostalStreetFileRegex();

    internal static Regex PostalStreetFile => PostalStreetFileRegex();

    [GeneratedRegex(@"^TU_COM_REFNIS-\d{8}(\.cut)?\.txt$")]
    private static partial Regex RefnisFile();

    [GeneratedRegex(@"^NIS6withnamefrom\d{8}(\.cut)?\.xlsx$")]
    private static partial Regex Nis6File();

    public IReadOnlyList<string> Countries => ["be"];

    public IReadOnlyList<string> SourceIds => [BelgiumRefnisSource.SourceId, BelgiumNis6Source.SourceId, BelgiumPostalStreetsSource.SourceId];

    public int Order => 10;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(BelgiumMapper).Assembly);

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        var refnis = inputs.TryGetValue(BelgiumRefnisSource.SourceId, out var r) && r.Find(RefnisFile()) is { } rf
            ? RefnisReader.Read(rf.Open()).ToList() : [];
        var nis6 = inputs.TryGetValue(BelgiumNis6Source.SourceId, out var n) && n.Find(Nis6File()) is { } nf
            ? Nis6Reader.Read(nf.Open()).ToList() : [];
        var streets = new List<PostalStreetRow>();
        if (inputs.TryGetValue(BelgiumPostalStreetsSource.SourceId, out var p))
            foreach (var file in p.Files.Where(f => PostalStreetFile.IsMatch(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal))
                streets.AddRange(PostalStreetsReader.Read(file.Open(), file.Name.Split('_')[0]));

        new Run(build, refnis, nis6, streets).Map();
        return Task.CompletedTask;
    }

    /// <summary>One mapping pass; holds the lookups the steps share.</summary>
    private sealed class Run(RefDataBuild build, List<RefnisRow> refnis, List<Nis6Row> nis6, List<PostalStreetRow> streets)
    {
        private readonly Dictionary<string, List<RefnisRow>> units = refnis
            .GroupBy(x => x.Code).ToDictionary(g => g.Key, g => g.OrderBy(x => x.From).ToList(), StringComparer.Ordinal);

        /// <summary>Codes whose place is another code's (a recode keeps the id of the code that minted it).</summary>
        private readonly Dictionary<string, string> recodedTo = new(StringComparer.Ordinal);

        /// <summary>NIS6 codes attached to an existing place (a demoted municipality), by NIS6 code.</summary>
        private readonly Dictionary<string, string> sectionPlace = new(StringComparer.Ordinal);

        private string countryPlace = string.Empty;

        public void Map()
        {
            MapCountry();
            if (units.Count > 0)
            {
                FindRecodes();
                foreach (var (code, rows) in units.OrderBy(u => u.Value[0].Level).ThenBy(u => u.Key, StringComparer.Ordinal))
                    MapUnit(code, rows);
                DeriveMergesAndRecodes();
                DeriveDemotions();
            }
            MapSections();
            MapPostalStreets();
        }

        // ---- the country ----

        private void MapCountry()
        {
            var country = build.Country("be");
            country.Alpha3 = "BEL";
            country.Numeric = "056";
            country.Profile = BelgiumProfile.Create();
            country.Source = [BelgiumRefnisSource.SourceId];

            countryPlace = build.Ledger.Mint(BelgiumRefnisSource.SourceId, "iso3166:BE", RefIds.Place("be", "iso3166", "BE"));
            country.Place = countryPlace;
            var place = build.Place(countryPlace);
            place.Periods = [new PlacePeriod { From = null, To = null, Kind = "be:country", Role = PlaceRole.Country, Parent = null }];
            // Country names are CLDR's (RD-DEF 10), never invented here.
            place.Names = TranslatedString.Of(
                ("nl", CldrNames.Territory("BE", "nl")), ("fr", CldrNames.Territory("BE", "fr")), ("de", CldrNames.Territory("BE", "de")));
            place.Names.Entries = [.. place.Names.Entries.Select(e => e with { Kind = TranslatedStringKind.Official, Source = "cldr" }),
                new TranslatedStringEntry("en", CldrNames.Territory("BE", "en")) { Kind = TranslatedStringKind.Exonym, Source = "cldr" }];
            place.OfficialLanguages = [new() { Lang = "nl" }, new() { Lang = "fr" }, new() { Lang = "de" }];
            place.Codes = [new PlaceCode { Scheme = "iso3166", Value = "BE" }];
            place.TimeZone = BelgiumProfile.Zone;
            place.Source = ["cldr"];
        }

        // ---- REFNIS units ----

        private string NisId(string code)
        {
            if (recodedTo.TryGetValue(code, out var target))
                return build.Ledger.Alias(BelgiumRefnisSource.SourceId, "nis:" + code, NisId(target));
            return build.Ledger.Mint(BelgiumRefnisSource.SourceId, "nis:" + code, RefIds.Place("be", "nis", code));
        }

        /// <summary>One code ends, one starts the same day, with the same names and no other participant: a recode (not a merge).</summary>
        private void FindRecodes()
        {
            foreach (var (code, rows) in units.Where(u => u.Value[0].Level == 4 && u.Value[0].From > RefnisStart))
            {
                var start = rows[0].From;
                var participants = Predecessors(code, start);
                if (participants.Count == 1 && units.TryGetValue(participants[0], out var old)
                    && Same(old[^1], rows[0]) && !units.Any(u => u.Key != code && u.Value[0].From == start && Predecessors(u.Key, start).Contains(participants[0])))
                    recodedTo[code] = participants[0];
            }
        }

        /// <summary>Codes that ended the day before <paramref name="start"/> and appear as NIS6 prefixes of <paramref name="code"/>'s sections.</summary>
        private List<string> Predecessors(string code, DateOnly start)
            => [.. nis6.Where(s => s.Municipality == code).Select(s => s.Code[..5]).Distinct(StringComparer.Ordinal)
                .Where(prefix => prefix != code && units.TryGetValue(prefix, out var rows) && rows[^1].To == start)
                .OrderBy(prefix => prefix, StringComparer.Ordinal)];

        private static bool Same(RefnisRow a, RefnisRow b) => a.NameNl == b.NameNl && a.NameFr == b.NameFr && a.NameDe == b.NameDe;

        private void MapUnit(string code, List<RefnisRow> rows)
        {
            var id = NisId(code);
            var place = build.Place(id);
            var kind = KindOf(rows[0].Level);
            var languages = OfficialLanguages(code);
            foreach (var row in rows)
            {
                var parent = row.Parent is null ? countryPlace : NisId(row.Parent);
                // REFNIS starts in 1970: a unit listed from 1970-01-01 existed before, from an unknown date (null = unknown, §4.2).
                place.Periods.Add(new PlacePeriod { From = row.From == RefnisStart ? null : row.From, To = row.To, Kind = kind, Role = RoleOf(row.Level), Parent = parent });
                AddNames(place.Names, row, languages);
            }
            place.Periods = MergePeriods(place.Periods);
            place.Codes.Add(new PlaceCode { Scheme = "nis", Value = code, From = rows[0].From == RefnisStart ? null : rows[0].From, To = rows[^1].To });
            place.OfficialLanguages = [.. languages.Select(l => new DatedLanguage { Lang = l })];
            place.TimeZone = BelgiumProfile.Zone;
            place.Source.Add(BelgiumRefnisSource.SourceId);
        }

        private static string KindOf(int level) => level switch
        {
            1 => "be:region",
            2 => "be:province",
            3 => "be:arrondissement",
            _ => "be:municipality",
        };

        private static PlaceRole RoleOf(int level) => level switch
        {
            1 => PlaceRole.Region,
            2 or 3 => PlaceRole.Subregion,
            _ => PlaceRole.Municipality,
        };

        /// <summary>The unit's official languages, from its region (Flanders nl; Wallonia fr, de for the German-speaking municipalities; Brussels fr, nl).</summary>
        private List<string> OfficialLanguages(string code)
        {
            if (GermanSpeaking.Contains(code)) return ["de"];
            var region = RegionOf(code, 0);
            return region switch
            {
                "02000" => ["nl"],
                "03000" => ["fr"],
                "04000" => ["fr", "nl"],
                _ => ["nl", "fr", "de"], // the country, and the unitary province of Brabant before 1995
            };
        }

        private string? RegionOf(string code, int depth)
        {
            if (depth > 6 || !units.TryGetValue(code, out var rows)) return null;
            var last = rows[^1];
            if (last.Level == 1) return code;
            return last.Parent is null ? null : RegionOf(last.Parent, depth + 1);
        }

        /// <summary>The official names in the official languages, the others as exonyms when they differ; level prefixes stripped.</summary>
        private static void AddNames(TranslatedString names, RefnisRow row, List<string> official)
        {
            var values = new[] { ("nl", Strip(row.NameNl, row.Level)), ("fr", Strip(row.NameFr, row.Level)), ("de", Strip(row.NameDe, row.Level)) };
            var officialValues = values.Where(v => official.Contains(v.Item1)).Select(v => v.Item2).ToHashSet(StringComparer.Ordinal);
            foreach (var lang in official)
                AddDated(names, lang, values.First(v => v.Item1 == lang).Item2, TranslatedStringKind.Official, row);
            foreach (var (lang, value) in values.Where(v => !official.Contains(v.Item1) && !officialValues.Contains(v.Item2) && v.Item2.Length > 0))
                AddDated(names, lang, value, TranslatedStringKind.Exonym, row);
        }

        /// <summary>Adds a dated entry, or extends the identical one that ends where this row starts (one name across several rows).</summary>
        private static void AddDated(TranslatedString names, string tag, string value, TranslatedStringKind kind, RefnisRow row)
        {
            if (value.Length == 0) return;
            var from = row.From == RefnisStart ? (DateOnly?)null : row.From;
            var index = names.Entries.FindIndex(e => e.Tag == tag && e.Value == value && e.Kind == kind && e.ValidTo == from);
            if (index >= 0)
                names.Entries[index] = names.Entries[index] with { ValidTo = row.To };
            else
                names.Entries.Add(new TranslatedStringEntry(tag, value) { Kind = kind, ValidFrom = from, ValidTo = row.To, Source = BelgiumRefnisSource.SourceId });
        }

        private static readonly string[] Prefixes =
        [
            "Provincie ", "Arrondissement ", "Provinz ", "Bezirk ",
            "Province de la ", "Province du ", "Province de ", "Province d’", "Province d'",
            "Arrondissement de la ", "Arrondissement du ", "Arrondissement de ", "Arrondissement d’", "Arrondissement d'",
        ];

        private static string Strip(string name, int level)
        {
            if (level is 2 or 3)
                foreach (var prefix in Prefixes)
                    if (name.StartsWith(prefix, StringComparison.Ordinal))
                        return name[prefix.Length..];
            return name;
        }

        private static List<PlacePeriod> MergePeriods(List<PlacePeriod> periods)
        {
            var merged = new List<PlacePeriod>();
            foreach (var period in periods.OrderBy(x => DateKeys.From(x.From)))
            {
                if (merged.Count > 0 && merged[^1] is var last && last.To == period.From && last.Kind == period.Kind && last.Parent == period.Parent && last.Role == period.Role)
                    last.To = period.To;
                else
                    merged.Add(period);
            }
            return merged;
        }

        // ---- lineage ----

        private void DeriveMergesAndRecodes()
        {
            foreach (var (code, rows) in units.Where(u => u.Value[0].Level == 4 && u.Value[0].From > RefnisStart).OrderBy(u => u.Key, StringComparer.Ordinal))
            {
                var start = rows[0].From;
                if (recodedTo.TryGetValue(code, out var old))
                {
                    var lineage = new RefLineageEvent
                    {
                        Id = RefIds.LineageEvent("be", start, LineageKind.Recode, "nis", code),
                        Country = "be", Date = start, Kind = LineageKind.Recode,
                        Operations = [new LineageOperation { Place = NisId(old), Op = LineageOp.Recode }],
                        Sources = [new LineageSource { Source = BelgiumRefnisSource.SourceId, Date = start, Note = $"{old} ends {start.AddDays(-1):yyyy-MM-dd}, {code} starts {start:yyyy-MM-dd} with the same names" }],
                    };
                    if (units[old][^1].Parent != rows[0].Parent)
                        lineage.Operations.Add(new LineageOperation { Place = NisId(old), Op = LineageOp.Reparent });
                    build.AddLineage(lineage);
                    continue;
                }

                var predecessors = Predecessors(code, start);
                if (predecessors.Count == 0)
                    continue;
                var merge = new RefLineageEvent
                {
                    Id = RefIds.LineageEvent("be", start, LineageKind.Merge, "nis", code),
                    Country = "be", Date = start, Kind = LineageKind.Merge,
                    Operations = [new LineageOperation { Place = NisId(code), Op = LineageOp.Create }],
                    Sources = [
                        new LineageSource { Source = BelgiumRefnisSource.SourceId, Date = start, Note = $"{string.Join(", ", predecessors)} end the day before {code} starts" },
                        new LineageSource { Source = BelgiumNis6Source.SourceId, Note = $"the sections of {code} carry the prefixes {string.Join(", ", predecessors)}" },
                    ],
                };
                foreach (var predecessor in predecessors)
                    merge.Operations.Add(new LineageOperation { Place = NisId(predecessor), Op = LineageOp.End, Into = NisId(code), Share = SuccessionShare.Whole });
                build.AddLineage(merge);
            }
        }

        /// <summary>
        /// An ended municipality that continues as one section of a continuing municipality: the same place, one level
        /// down (absorb + demote). 2025 (Borsbeek) by NIS6 prefix; 1977 by name within the arrondissement.
        /// </summary>
        private void DeriveDemotions()
        {
            var startedOn = units.Where(u => u.Value[0].Level == 4).GroupBy(u => u.Value[0].From).ToDictionary(g => g.Key, g => g.Select(u => u.Key).ToHashSet(StringComparer.Ordinal));
            var merged = build.Lineage.Values.Where(e => e.Kind == LineageKind.Merge).SelectMany(e => e.Operations.Where(o => o.Op == LineageOp.End).Select(o => o.Place)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var recoded = recodedTo.Values.ToHashSet(StringComparer.Ordinal);

            foreach (var (code, rows) in units.Where(u => u.Value[0].Level == 4 && u.Value[^1].To is not null).OrderBy(u => u.Key, StringComparer.Ordinal))
            {
                var end = rows[^1].To!.Value;
                if (merged.Contains(NisId(code)) || recoded.Contains(code))
                    continue;

                List<Nis6Row> candidates;
                if (end == Fusion1977)
                {
                    // RS22: the section of the same name whose (pre-merger) municipality lay in the same arrondissement.
                    var arrondissement = rows[^1].Parent;
                    var key = Key(rows[^1].NameNl);
                    candidates = [.. nis6.Where(s => Key(s.NameNl) == key && units.TryGetValue(s.Code[..5], out var m)
                        && m.Any(x => x.Parent == arrondissement && x.From <= end && (x.To is null || x.To > end)))];
                }
                else
                {
                    candidates = [.. nis6.Where(s => s.Code[..5] == code && !(startedOn.TryGetValue(end, out var started) && started.Contains(s.Municipality)))];
                }

                if (candidates.Count == 1 && (end == Fusion1977 || Key(candidates[0].NameNl) == Key(rows[^1].NameNl)))
                {
                    var section = candidates[0];
                    var into = end == Fusion1977 ? section.Code[..5] : section.Municipality;
                    sectionPlace[section.Code] = NisId(code);
                    build.AddLineage(new RefLineageEvent
                    {
                        Id = RefIds.LineageEvent("be", end, LineageKind.Absorb, "nis", code),
                        Country = "be", Date = end, Kind = LineageKind.Absorb,
                        Operations =
                        [
                            new LineageOperation { Place = NisId(into), Op = LineageOp.Receive },
                            new LineageOperation { Place = NisId(code), Op = LineageOp.Demote, Into = NisId(into) },
                        ],
                        Sources =
                        [
                            new LineageSource { Source = BelgiumRefnisSource.SourceId, Date = end, Note = $"{code} ends {end.AddDays(-1):yyyy-MM-dd}" },
                            new LineageSource { Source = BelgiumNis6Source.SourceId, Note = $"section {section.Code} {section.NameNl}" },
                        ],
                    });
                }
                else
                {
                    // Q-R4: recorded unresolved, the place ends, flagged; never guessed.
                    build.AddLineage(new RefLineageEvent
                    {
                        Id = RefIds.LineageEvent("be", end, LineageKind.Absorb, "nis", code),
                        Country = "be", Date = end, Kind = LineageKind.Absorb, Unresolved = true,
                        Operations = [new LineageOperation { Place = NisId(code), Op = LineageOp.End }],
                        Sources = [new LineageSource { Source = BelgiumRefnisSource.SourceId, Date = end, Note = $"{code} ends; {candidates.Count} matching sections" }],
                    });
                    build.Warnings.Add($"be: {code} {rows[^1].NameNl} ended {end:yyyy-MM-dd} with {candidates.Count} matching sections: unresolved (Q-R4).");
                }
            }
        }

        // ---- NIS6 sections ----

        private void MapSections()
        {
            foreach (var section in nis6.OrderBy(s => s.Code, StringComparer.Ordinal))
            {
                var prefix = section.Code[..5];
                var current = section.Municipality;
                var kind = current == "11002" ? "be:district" : "be:section";
                var currentStart = units.TryGetValue(current, out var cr) ? cr[0].From : (DateOnly?)null;

                if (sectionPlace.TryGetValue(section.Code, out var demoted))
                {
                    // The demoted municipality continues as this section (identity rule, RD3).
                    var place = build.Place(demoted);
                    var end = place.Periods.Max(x => x.To);
                    if (prefix == current || currentStart is null || currentStart <= end)
                        place.Periods.Add(new PlacePeriod { From = end, To = null, Kind = kind, Role = PlaceRole.Submunicipality, Parent = NisId(current) });
                    else
                    {
                        place.Periods.Add(new PlacePeriod { From = end, To = currentStart, Kind = kind, Role = PlaceRole.Submunicipality, Parent = NisId(prefix) });
                        place.Periods.Add(new PlacePeriod { From = currentStart, To = null, Kind = kind, Role = PlaceRole.Submunicipality, Parent = NisId(current) });
                    }
                    place.Periods = MergePeriods(place.Periods);
                    place.Codes.Add(new PlaceCode { Scheme = "nis6", Value = section.Code });
                    place.Source.Add(BelgiumNis6Source.SourceId);
                    continue;
                }

                var id = build.Ledger.Mint(BelgiumNis6Source.SourceId, "nis6:" + section.Code, RefIds.Place("be", "nis6", section.Code));
                var sectionPlaceDoc = build.Place(id);
                // Sections exist from the 1977 fusion, or from their municipality's creation when that is later.
                var prefixStart = units.TryGetValue(prefix, out var pr) ? pr[0].From : Fusion1977;
                var from = prefixStart > Fusion1977 ? prefixStart : Fusion1977;
                if (prefix == current || recodedTo.TryGetValue(current, out var old) && old == prefix || currentStart is null || currentStart <= from)
                    sectionPlaceDoc.Periods = [new PlacePeriod { From = from, To = null, Kind = kind, Role = PlaceRole.Submunicipality, Parent = NisId(current) }];
                else
                    sectionPlaceDoc.Periods =
                    [
                        new PlacePeriod { From = from, To = currentStart, Kind = "be:section", Role = PlaceRole.Submunicipality, Parent = NisId(prefix) },
                        new PlacePeriod { From = currentStart, To = null, Kind = kind, Role = PlaceRole.Submunicipality, Parent = NisId(current) },
                    ];
                var languages = OfficialLanguages(current);
                sectionPlaceDoc.Names = new TranslatedString();
                foreach (var lang in languages)
                {
                    var value = lang == "fr" ? section.NameFr : section.NameNl;
                    sectionPlaceDoc.Names.Entries.Add(new TranslatedStringEntry(lang, ProperName(value, current)) { Kind = TranslatedStringKind.Official, Source = BelgiumNis6Source.SourceId });
                }
                sectionPlaceDoc.OfficialLanguages = [.. languages.Select(l => new DatedLanguage { Lang = l })];
                sectionPlaceDoc.Codes = [new PlaceCode { Scheme = "nis6", Value = section.Code }];
                sectionPlaceDoc.TimeZone = BelgiumProfile.Zone;
                sectionPlaceDoc.Source = [BelgiumNis6Source.SourceId];
            }
        }

        /// <summary>
        /// NIS6 writes names in capitals. The proper spelling comes from data that has it: the postal-streets localities or
        /// a REFNIS unit of the same name; otherwise a title case that keeps Dutch and French particles lower case.
        /// </summary>
        private string ProperName(string upper, string municipality)
        {
            var key = Key(upper);
            foreach (var row in streets.Where(s => s.CityId == municipality))
            {
                foreach (var candidate in row.PostalNl.Split('/').Append(row.CityPartFr).Append(row.CityNl).Append(row.CityFr))
                    if (candidate.Length > 0 && Key(candidate) == key && candidate != candidate.ToUpperInvariant())
                        return candidate;
            }
            foreach (var rows in units.Values)
                foreach (var row in rows.Where(x => x.Level == 4))
                    foreach (var name in new[] { row.NameNl, row.NameFr })
                        if (Key(name) == key) return name;
            return TitleCase(upper);
        }

        private static readonly HashSet<string> Particles = ["aan", "de", "den", "der", "het", "op", "ter", "ten", "in", "bij", "sur", "la", "le", "les", "lez", "du", "des", "en", "et", "sous", "lès"];

        internal static string TitleCase(string upper)
        {
            var lower = upper.ToLowerInvariant();
            var chars = lower.ToCharArray();
            var start = true;
            var word = new System.Text.StringBuilder();
            var result = new System.Text.StringBuilder();
            for (var i = 0; i <= chars.Length; i++)
            {
                if (i == chars.Length || chars[i] is ' ' or '-' or '\'')
                {
                    var w = word.ToString();
                    result.Append(result.Length == 0 || !Particles.Contains(w) ? (w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w) : w);
                    if (i < chars.Length) result.Append(chars[i]);
                    word.Clear();
                    start = true;
                    continue;
                }
                word.Append(chars[i]);
                start = false;
            }
            _ = start;
            return result.ToString();
        }

        private static string Key(string name) => string.Concat(RefSearchKeys.Tokenize(RefSearchKeys.Fold(name)));

        // ---- BeST postal streets: postal localities, Walloon parts of municipality, streets ----

        private string Municipality(string cityNo) => NisId(cityNo);

        private void MapPostalStreets()
        {
            var regionCode = new Dictionary<string, string> { ["Flanders"] = "vlg", ["Brussels"] = "bru", ["Wallonia"] = "wal" };
            var regionLanguages = new Dictionary<string, List<string>> { ["Flanders"] = ["nl"], ["Brussels"] = ["fr", "nl"], ["Wallonia"] = ["fr"] };

            // Walloon parts of municipality (Q-R16): places of their own, linked to their municipality through this file.
            foreach (var part in streets.Where(s => s.CityPartId.Length > 0).GroupBy(s => s.CityPartId).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var row = part.First();
                var id = build.Ledger.Mint(BelgiumPostalStreetsSource.SourceId, "best-wal-part:" + part.Key, RefIds.Place("be", "walpart", part.Key));
                var place = build.Place(id);
                place.Periods = [new PlacePeriod { From = null, To = null, Kind = "be:locality", Role = PlaceRole.Locality, Parent = Municipality(row.CityId) }];
                place.Names = new TranslatedString();
                var languages = GermanSpeaking.Contains(row.CityId) ? new List<string> { "de" } : ["fr"];
                foreach (var (lang, value) in new[] { ("fr", row.CityPartFr), ("de", row.CityPartDe), ("nl", row.CityPartNl) })
                    if (value.Length > 0 && !place.Names.Entries.Any(e => e.Value == value))
                        place.Names.Entries.Add(new TranslatedStringEntry(lang, value) { Kind = languages.Contains(lang) ? TranslatedStringKind.Official : TranslatedStringKind.Exonym, Source = BelgiumPostalStreetsSource.SourceId });
                place.OfficialLanguages = [.. languages.Select(l => new DatedLanguage { Lang = l })];
                place.Codes = [new PlaceCode { Scheme = "best-wal-part", Value = part.Key }];
                place.TimeZone = BelgiumProfile.Zone;
                place.Source = [BelgiumPostalStreetsSource.SourceId];
            }

            foreach (var row in streets)
            {
                var municipality = Municipality(row.CityId);
                var languages = GermanSpeaking.Contains(row.CityId) ? ["de"] : regionLanguages[row.Region];
                var postalId = build.Ledger.Mint(BelgiumPostalStreetsSource.SourceId, "postcode:" + row.PostalCode, RefIds.PostalCode("be", row.PostalCode));
                var postal = build.PostalCode(postalId, "be", row.PostalCode);
                if (!postal.Source.Contains(BelgiumPostalStreetsSource.SourceId)) postal.Source.Add(BelgiumPostalStreetsSource.SourceId);

                var locality = AddLocality(postal, row, municipality, languages);

                var region = regionCode[row.Region];
                var streetId = build.Ledger.Mint(BelgiumPostalStreetsSource.SourceId, $"best-{region}:{row.StreetId}", RefIds.Street("be", region, row.StreetId));
                var street = build.Street(streetId, "be");
                if (street.Names.Entries.Count == 0)
                {
                    foreach (var (lang, value) in new[] { ("nl", row.StreetNl), ("fr", row.StreetFr), ("de", row.StreetDe) }.OrderBy(v => languages.IndexOf(v.Item1) is var i and >= 0 ? i : 9))
                        if (value.Length > 0)
                            street.Names.Entries.Add(new TranslatedStringEntry(lang, value)
                            {
                                Kind = languages.Contains(lang) ? TranslatedStringKind.Official : TranslatedStringKind.Exonym,
                                Source = BelgiumPostalStreetsSource.SourceId,
                            });
                    street.Codes = [new PlaceCode { Scheme = "best-" + region, Value = row.StreetId }];
                    street.Type = StreetType.Streetname;
                    street.Source = [BelgiumPostalStreetsSource.SourceId];
                }
                var period = street.Periods.FirstOrDefault(x => x.Municipality == municipality);
                if (period is null)
                    street.Periods.Add(period = new StreetPeriod { Municipality = municipality, Locality = locality.Place });
                if (!period.PostalCodes.Contains(postalId, StringComparer.OrdinalIgnoreCase))
                    period.PostalCodes.Add(postalId);
            }
        }

        /// <summary>
        /// The (postal code, name) pair a row belongs to. Flanders: one of the slash-joined postal names (the main
        /// municipality in capitals); Wallonia: the part of municipality; Brussels: the municipality itself.
        /// </summary>
        private PostalLocality AddLocality(RefPostalCode postal, PostalStreetRow row, string municipality, List<string> languages)
        {
            string name;
            string? place = null;
            var main = false;
            var names = new TranslatedString();
            if (row.Region == "Wallonia" && row.CityPartFr.Length > 0)
            {
                name = row.CityPartFr;
                main = Key(row.CityPartFr) == Key(row.CityFr);
                place = build.Ledger.Find("best-wal-part:" + row.CityPartId);
                names.Entries.Add(new TranslatedStringEntry(languages[0], name) { Kind = TranslatedStringKind.Official, Source = BelgiumPostalStreetsSource.SourceId });
            }
            else if (row.Region == "Flanders" && row.PostalNl.Length > 0)
            {
                // The postal names of the code; the locality is the one matching the street's section, else the main one.
                var candidates = row.PostalNl.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var mainName = candidates.FirstOrDefault(c => c == c.ToUpperInvariant()) ?? candidates[0];
                name = mainName;
                main = mainName == mainName.ToUpperInvariant();
                if (main)
                    name = Key(mainName) == Key(row.CityNl) ? row.CityNl : TitleCase(mainName);
                names.Entries.Add(new TranslatedStringEntry("nl", name) { Kind = TranslatedStringKind.Official, Source = BelgiumPostalStreetsSource.SourceId });
                // The other names of the code are localities too, each in its own right (9750 = Huise, Ouwegem, Zingem).
                foreach (var other in candidates.Where(c => c != mainName))
                    EnsureLocality(postal, other, municipality, other == other.ToUpperInvariant(), [new TranslatedStringEntry("nl", other == other.ToUpperInvariant() ? TitleCase(other) : other) { Kind = TranslatedStringKind.Official, Source = BelgiumPostalStreetsSource.SourceId }]);
            }
            else
            {
                name = languages[0] == "nl" ? row.CityNl : row.CityFr;
                main = true;
                foreach (var lang in languages)
                {
                    var value = lang == "nl" ? row.CityNl : lang == "de" ? row.CityDe : row.CityFr;
                    if (value.Length > 0)
                        names.Entries.Add(new TranslatedStringEntry(lang, value) { Kind = TranslatedStringKind.Official, Source = BelgiumPostalStreetsSource.SourceId });
                }
            }
            return EnsureLocality(postal, name, municipality, main, names.Entries, place);
        }

        private PostalLocality EnsureLocality(RefPostalCode postal, string name, string municipality, bool main, List<TranslatedStringEntry> names, string? place = null)
        {
            var key = Key(name);
            var locality = postal.Localities.FirstOrDefault(l => l.Key == key);
            if (locality is not null)
                return locality;

            place ??= SectionNamed(key, municipality) ?? (Key(build.Places.GetValueOrDefault(municipality)?.Names.Resolve(["nl", "fr"]) ?? string.Empty) == key ? municipality : null);
            locality = new PostalLocality
            {
                Key = key,
                Names = new TranslatedString { Entries = names },
                Place = place,
                Main = main,
                Municipalities = MunicipalitiesOf(place, municipality),
            };
            postal.Localities.Add(locality);
            return locality;
        }

        /// <summary>The section of <paramref name="municipality"/> with that name (current periods), if any.</summary>
        private string? SectionNamed(string key, string municipality)
            => build.Places.Values
                .Where(p => p.Periods.Any(x => x.Role == PlaceRole.Submunicipality && x.Parent == municipality && x.To is null))
                .Where(p => p.Names.Entries.Any(e => Key(e.Value) == key))
                .Select(p => p.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .FirstOrDefault();

        /// <summary>
        /// The municipality a locality lay in over time: its section's parents while it was a section (9770 Kruishoutem:
        /// 45017 until 2018, 45068 from 2019); otherwise the current municipality since its creation.
        /// </summary>
        private List<DatedLink> MunicipalitiesOf(string? place, string municipality)
        {
            if (place is not null && build.Places.TryGetValue(place, out var p) && p.Periods.Any(x => x.Role == PlaceRole.Submunicipality))
                return [.. p.Periods.Where(x => x.Role == PlaceRole.Submunicipality && x.Parent is not null)
                    .Select(x => new DatedLink { Id = x.Parent!, From = x.From, To = x.To })];
            var start = build.Places.TryGetValue(municipality, out var m) ? m.Periods.Where(x => x.Role == PlaceRole.Municipality).Select(x => x.From).DefaultIfEmpty(null).Min() : null;
            return [new DatedLink { Id = municipality, From = start, To = null }];
        }
    }
}
