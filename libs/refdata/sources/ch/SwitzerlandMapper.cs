using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Switzerland;

/// <summary>
/// Switzerland (PRD §4.4, plan R8). Cantons, districts and communes from the BFS commune register; lineage from its
/// mutations, derived structurally per mutation number and terminal commune:
/// <list type="bullet">
/// <item><b>Merge</b> (Grolley + Ponthaux → Grolley-Ponthaux 2239, 2025): the terminal BFS number is not among the
/// initial ones; every initial commune ends into it.</item>
/// <item><b>Absorb</b> (Chur 3901 absorbs Tschiertschen-Praden, 2025): the terminal continues under its own number and
/// receives the others, which end into it.</item>
/// <item><b>Recode</b> (Moutier 700 → 6831, moving from canton BE to JU on 2026-01-01): one commune, one new number,
/// nothing else: the same place gets a dated code (the id keeps the number that minted it) and a reparent.</item>
/// <item>A commune mapped onto itself with a new name or parent is a <b>rename</b> or <b>reparent</b>; one with only a
/// new historicization number (a boundary adjustment such as Neuendorf/Oberbuchsiten, 2025) gives no lineage: the
/// register does not say which way the territory went.</item>
/// </list>
/// Streets and postal localities come from swisstopo's street directory: ESID streets, bilingual names split per
/// language ("Aarbergstrasse / Rue d'Aarberg"), several postal localities per street ("7017 Flims Dorf, 7018 Flims
/// Waldhaus"); planned streets are skipped. Every place carries <c>Europe/Zurich</c>.
/// </summary>
internal sealed partial class SwitzerlandMapper : IReferenceDataMapper
{
    private static readonly DateOnly HistoryStart = new(1960, 1, 1);

    [GeneratedRegex(@"^bfs-communes-snapshot-[0-9-]+(\.cut)?\.csv$")]
    private static partial Regex SnapshotFile();

    [GeneratedRegex(@"^bfs-communes-mutations(-[0-9-]+)?(\.cut)?\.csv$")]
    private static partial Regex MutationsFile();

    [GeneratedRegex(@"^(?<code>[0-9]{4}) (?<name>.+)$")]
    private static partial Regex ZipLabel();

    public IReadOnlyList<string> Countries => ["ch"];
    public IReadOnlyList<string> SourceIds => [SwitzerlandCommunesSource.SourceId, SwitzerlandStreetsSource.SourceId];
    public int Order => 10;
    public bool IsCountryAdapter => true;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(SwitzerlandMapper).Assembly);

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        var snapshot = new List<SnapshotRow>();
        var mutations = new List<MutationRow>();
        if (inputs.TryGetValue(SwitzerlandCommunesSource.SourceId, out var bfs))
        {
            if (bfs.Find(SnapshotFile()) is { } s)
                using (var stream = s.Open())
                    snapshot.AddRange(RefCsv.ReadRecords(stream, ',').Select(SnapshotRow.From));
            if (bfs.Find(MutationsFile()) is { } m)
                using (var stream = m.Open())
                    mutations.AddRange(RefCsv.ReadRecords(stream, ',').Select(MutationRow.From));
        }
        var streets = new List<RefCsvRow>();
        if (inputs.TryGetValue(SwitzerlandStreetsSource.SourceId, out var str) && str.Find(SwitzerlandStreetsSource.StreetFile()) is { } sf)
            using (var stream = sf.Open())
                streets.AddRange(RefCsv.ReadRecords(stream, ';'));

        new Run(build, snapshot, mutations, streets).Map();
        return Task.CompletedTask;
    }

    private sealed record SnapshotRow(string Hist, string Bfs, DateOnly? ValidFrom, DateOnly? ValidTo, int Level, string Parent, string Name, string ShortName, bool NoDistricts)
    {
        public static SnapshotRow From(RefCsvRow r) => new(
            r["HistoricalCode"], r["BfsCode"], Date(r["ValidFrom"], "dd.MM.yyyy"), Date(r["ValidTo"], "dd.MM.yyyy"),
            int.TryParse(r["Level"], NumberStyles.None, CultureInfo.InvariantCulture, out var level) ? level : 0,
            r["Parent"], r["Name"], r["ShortName"], r["Rec_Type_de"].Length > 0);
    }

    private sealed record MutationRow(string Number, DateOnly Date, string InitHist, string InitBfs, string InitName, string InitParentHist, string InitParentName, string InitStep,
        string TermHist, string TermBfs, string TermName, string TermParentHist, string TermParentName, string TermStep)
    {
        public static MutationRow From(RefCsvRow r) => new(
            r["MutationNumber"], RefAdapterKit.Date(r["MutationDate"], "dd.MM.yyyy") ?? throw new InvalidDataException($"BFS mutation {r["MutationNumber"]} has no date."),
            r["InitialHistoricalCode"], r["InitialCode"], r["InitialName"], r["InitialParentHistoricalCode"], r["InitialParentName"], r["InitialStep"],
            r["TerminalHistoricalCode"], r["TerminalCode"], r["TerminalName"], r["TerminalParentHistoricalCode"], r["TerminalParentName"], r["TerminalStep"]);
    }

    /// <summary>What a mutation row says about one commune at one date: its state until then, or from then on.</summary>
    private sealed record Observation(DateOnly Date, bool From, string ParentHist, string ParentName, string Name, string Hist, string Bfs);

    private sealed class Run(RefDataBuild build, List<SnapshotRow> snapshot, List<MutationRow> mutations, List<RefCsvRow> streets)
    {
        private const string Bfs = SwitzerlandCommunesSource.SourceId;
        private const string Streets = SwitzerlandStreetsSource.SourceId;

        /// <summary>A new BFS number whose commune is an older number's (a recode keeps the id the old number minted).</summary>
        private readonly Dictionary<string, (string Old, DateOnly Date)> recodedFrom = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> cantonByHist = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> cantonShortById = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> districtByHist = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> parentCanton = new(StringComparer.OrdinalIgnoreCase);
        private string countryPlace = string.Empty;

        public void Map()
        {
            countryPlace = CountryPlace(build, "ch", Bfs, SwitzerlandProfile.Zone, "de", "fr", "it", "rm");
            Country(build, "ch", "CHE", "756", SwitzerlandProfile.Create(), Bfs);
            MapCantonsAndDistricts();
            FindRecodes();
            MapCommunes();
            MapStreets();
        }

        // ---- cantons and districts ----

        private void MapCantonsAndDistricts()
        {
            foreach (var row in snapshot.Where(r => r.Level == 1).OrderBy(r => r.Bfs, StringComparer.Ordinal))
            {
                var id = build.Ledger.Mint(Bfs, "bfs-canton:" + row.Bfs, RefIds.Place("ch", "canton", row.ShortName));
                cantonByHist[row.Hist] = id;
                cantonShortById[id] = row.ShortName;
                var languages = SwitzerlandProfile.CantonLanguages.GetValueOrDefault(row.ShortName) ?? ["de"];
                var place = build.Place(id);
                place.Periods = [new PlacePeriod { From = Since(row.ValidFrom), To = row.ValidTo, Kind = "ch:canton", Role = PlaceRole.Region, Parent = countryPlace }];
                place.Names = SplitNames(row.Name, languages, Bfs, TranslatedStringKind.Official);
                place.OfficialLanguages = [.. languages.Select(l => new DatedLanguage { Lang = l })];
                place.Codes = [new PlaceCode { Scheme = "bfs-canton", Value = row.Bfs }, new PlaceCode { Scheme = "iso3166-2", Value = "CH-" + row.ShortName }];
                place.TimeZone = SwitzerlandProfile.Zone;
                AddSource(place.Source, Bfs);
            }

            foreach (var row in snapshot.Where(r => r.Level == 2).OrderBy(r => r.Bfs, StringComparer.Ordinal))
            {
                if (!cantonByHist.TryGetValue(row.Parent, out var canton))
                {
                    build.Warnings.Add($"{Bfs}: district {row.Bfs} names canton {row.Parent}, which the snapshot lacks.");
                    continue;
                }
                // "Kanton ohne Bezirksunterteilung": a register row, not a district; its communes hang off the canton.
                if (row.NoDistricts)
                {
                    districtByHist[row.Hist] = canton;
                    continue;
                }
                var id = build.Ledger.Mint(Bfs, "bfs-district:" + row.Bfs, RefIds.Place("ch", "district", row.Bfs));
                districtByHist[row.Hist] = id;
                parentCanton[id] = canton;
                var languages = SwitzerlandProfile.CantonLanguages.GetValueOrDefault(cantonShortById[canton]) ?? ["de"];
                var place = build.Place(id);
                place.Periods = [new PlacePeriod { From = Since(row.ValidFrom), To = row.ValidTo, Kind = "ch:district", Role = PlaceRole.Subregion, Parent = canton }];
                place.Names = SplitNames(row.Name, languages, Bfs, TranslatedStringKind.Official);
                place.OfficialLanguages = [.. languages.Select(l => new DatedLanguage { Lang = l })];
                place.Codes = [new PlaceCode { Scheme = "bfs-district", Value = row.Bfs }, new PlaceCode { Scheme = "bfs-hist", Value = row.Hist, From = Since(row.ValidFrom), To = row.ValidTo }];
                place.TimeZone = SwitzerlandProfile.Zone;
                AddSource(place.Source, Bfs);
            }
        }

        /// <summary>The district (or canton without districts) a historicization number names; a retired one becomes a place of its own.</summary>
        private string District(string hist, string name)
        {
            if (districtByHist.TryGetValue(hist, out var id))
                return id;
            id = build.Ledger.Mint(Bfs, "bfs-district-hist:" + hist, RefIds.Place("ch", "district-hist", hist));
            var place = build.Place(id);
            // Its end and canton are unknown: the mutations name it, the snapshot does not (retired, or outside a cut).
            place.Periods = [new PlacePeriod { Kind = "ch:district", Role = PlaceRole.Subregion }];
            place.Names = new TranslatedString { Entries = [Name("de", name, TranslatedStringKind.Official, Bfs)] };
            place.Codes = [new PlaceCode { Scheme = "bfs-hist", Value = hist }];
            place.TimeZone = SwitzerlandProfile.Zone;
            AddSource(place.Source, Bfs);
            build.Warnings.Add($"{Bfs}: district {hist} ({name}) is not in the snapshot; kept without a canton or an end date.");
            return districtByHist[hist] = id;
        }

        // ---- communes ----

        private string CommuneId(string bfs)
        {
            if (recodedFrom.TryGetValue(bfs, out var old))
                return build.Ledger.Alias(Bfs, "bfs:" + bfs, CommuneId(old.Old));
            return build.Ledger.Mint(Bfs, "bfs:" + bfs, RefIds.Place("ch", "bfs", bfs));
        }

        private IEnumerable<IGrouping<(string Number, DateOnly Date), MutationRow>> Mutations()
            => mutations.GroupBy(m => (m.Number, m.Date)).OrderBy(g => g.Key.Date).ThenBy(g => int.TryParse(g.Key.Number, out var n) ? n : 0);

        /// <summary>One commune, one new number, no other participant and no abolition: a recode, not a merge.</summary>
        private void FindRecodes()
        {
            foreach (var mutation in Mutations())
                foreach (var terminal in mutation.GroupBy(m => m.TermBfs))
                {
                    var inits = terminal.ToList();
                    if (inits.Count == 1 && inits[0].InitBfs != terminal.Key && inits[0].InitStep != "29"
                        && mutation.Count(m => m.InitBfs == inits[0].InitBfs) == 1)
                        recodedFrom[terminal.Key] = (inits[0].InitBfs, mutation.Key.Date);
                }
        }

        private void MapCommunes()
        {
            var observations = new Dictionary<string, List<Observation>>(StringComparer.OrdinalIgnoreCase);
            var created = new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase);
            var ended = new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase);
            void Observe(string id, Observation o)
            {
                if (!observations.TryGetValue(id, out var list)) observations[id] = list = [];
                list.Add(o);
            }

            foreach (var mutation in Mutations())
            {
                var date = mutation.Key.Date;
                foreach (var row in mutation)
                {
                    Observe(CommuneId(row.InitBfs), new Observation(date, false, row.InitParentHist, row.InitParentName, row.InitName, row.InitHist, row.InitBfs));
                    Observe(CommuneId(row.TermBfs), new Observation(date, true, row.TermParentHist, row.TermParentName, row.TermName, row.TermHist, row.TermBfs));
                }

                foreach (var terminal in mutation.GroupBy(m => m.TermBfs).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    var target = CommuneId(terminal.Key);
                    var inits = terminal.ToList();
                    var source = new LineageSource { Source = Bfs, Date = date, Note = $"BFS mutation {mutation.Key.Number}" };
                    if (recodedFrom.TryGetValue(terminal.Key, out var recode) && recode.Date == date)
                    {
                        var lineage = new RefLineageEvent
                        {
                            Id = RefIds.LineageEvent("ch", date, LineageKind.Recode, "bfs", terminal.Key),
                            Country = "ch", Date = date, Kind = LineageKind.Recode,
                            Operations = [new LineageOperation { Place = target, Op = LineageOp.Recode }],
                            Sources = [source],
                        };
                        if (inits[0].InitParentHist != inits[0].TermParentHist)
                            lineage.Operations.Add(new LineageOperation { Place = target, Op = LineageOp.Reparent });
                        build.AddLineage(lineage);
                        continue;
                    }

                    var continuing = inits.Any(i => i.InitBfs == terminal.Key);
                    var others = inits.Where(i => i.InitBfs != terminal.Key).ToList();
                    if (others.Count == 0)
                    {
                        var self = inits[0];
                        var ops = new List<LineageOperation>();
                        if (self.InitName != self.TermName) ops.Add(new LineageOperation { Place = target, Op = LineageOp.Rename });
                        if (self.InitParentHist != self.TermParentHist) ops.Add(new LineageOperation { Place = target, Op = LineageOp.Reparent });
                        if (ops.Count == 0)
                            continue; // a boundary adjustment: the register does not say which way the territory went
                        var kind = ops[0].Op == LineageOp.Rename ? LineageKind.Rename : LineageKind.Reparent;
                        build.AddLineage(new RefLineageEvent
                        {
                            Id = RefIds.LineageEvent("ch", date, kind, "bfs", terminal.Key),
                            Country = "ch", Date = date, Kind = kind, Operations = ops, Sources = [source],
                        });
                        continue;
                    }

                    var merge = new RefLineageEvent
                    {
                        Id = RefIds.LineageEvent("ch", date, continuing ? LineageKind.Absorb : LineageKind.Merge, "bfs", terminal.Key),
                        Country = "ch", Date = date, Kind = continuing ? LineageKind.Absorb : LineageKind.Merge,
                        Operations = [new LineageOperation { Place = target, Op = continuing ? LineageOp.Receive : LineageOp.Create }],
                        Sources = [source],
                    };
                    if (!continuing)
                        created[target] = date;
                    foreach (var other in others.OrderBy(o => o.InitBfs, StringComparer.Ordinal))
                    {
                        var share = mutation.Where(m => m.InitBfs == other.InitBfs).Select(m => m.TermBfs).Distinct().Count() > 1 ? SuccessionShare.Part : SuccessionShare.Whole;
                        var predecessor = CommuneId(other.InitBfs);
                        merge.Operations.Add(new LineageOperation { Place = predecessor, Op = LineageOp.End, Into = target, Share = share });
                        if (!mutation.Any(m => m.TermBfs == other.InitBfs))
                            ended[predecessor] = date;
                    }
                    build.AddLineage(merge);
                }
            }

            foreach (var row in snapshot.Where(r => r.Level == 3))
                Observe(CommuneId(row.Bfs), new Observation(row.ValidFrom ?? HistoryStart, true, row.Parent, string.Empty, row.Name, row.Hist, row.Bfs) { });

            var current = snapshot.Where(r => r.Level == 3).ToDictionary(r => CommuneId(r.Bfs), StringComparer.OrdinalIgnoreCase);
            foreach (var (id, list) in observations.OrderBy(o => o.Key, StringComparer.Ordinal))
                MapCommune(id, list, created.TryGetValue(id, out var c) ? c : null, ended.TryGetValue(id, out var e) ? e : null, current.GetValueOrDefault(id));
        }

        private void MapCommune(string id, List<Observation> observations, DateOnly? created, DateOnly? ended, SnapshotRow? current)
        {
            var place = build.Place(id);
            var mutationDates = observations.Where(o => current is null || o.Hist != current.Hist || o.From is false).Select(o => o.Date)
                .Where(d => d != created && d != ended).Distinct().Order().ToList();
            var bounds = new List<DateOnly?> { created };
            bounds.AddRange(mutationDates.Where(d => (created is null || d > created) && (ended is null || d < ended)).Select(d => (DateOnly?)d));
            bounds.Add(ended);

            var periods = new List<PlacePeriod>();
            var names = new List<(DateOnly? From, DateOnly? To, string Name, string Lang)>();
            for (var i = 0; i < bounds.Count - 1; i++)
            {
                var from = bounds[i];
                var to = bounds[i + 1];
                var state = observations.Where(o => o.From && o.Date == from).LastOrDefault()
                    ?? observations.Where(o => !o.From && o.Date == to).LastOrDefault()
                    ?? (to is null && current is not null ? new Observation(HistoryStart, true, current.Parent, string.Empty, current.Name, current.Hist, current.Bfs) : null)
                    ?? observations.Where(o => !o.From && (to is null || o.Date >= to)).OrderBy(o => o.Date).FirstOrDefault()
                    ?? observations[0];
                var parent = District(state.ParentHist, state.ParentName);
                // A new historicization number under the same parent (an absorption, a boundary change) is no new period.
                if (periods.Count > 0 && periods[^1].Parent == parent)
                    periods[^1].To = to;
                else
                    periods.Add(new PlacePeriod { From = from, To = to, Kind = "ch:municipality", Role = PlaceRole.Municipality, Parent = parent });
                var lang = Languages(parent)[0];
                if (names.Count > 0 && names[^1].Name == state.Name)
                    names[^1] = names[^1] with { To = to };
                else
                    names.Add((from, to, state.Name, lang));
            }
            place.Periods = periods;
            var dated = names.Count > 1;
            place.Names = new TranslatedString
            {
                Entries = [.. names.Select(n => Name(n.Lang, n.Name, n.To is null ? TranslatedStringKind.Official : TranslatedStringKind.Historic, Bfs, dated ? n.From : null, dated ? n.To : null))],
            };
            place.OfficialLanguages = [.. Languages(periods[^1].Parent).Select(l => new DatedLanguage { Lang = l })];

            // Codes: every BFS number with its validity (a recode closes the old one), every historicization number seen.
            var codes = new List<PlaceCode>();
            foreach (var bfs in observations.Select(o => o.Bfs).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var recodedTo = recodedFrom.FirstOrDefault(r => r.Value.Old == bfs);
                var from = recodedFrom.TryGetValue(bfs, out var rc) ? rc.Date : (DateOnly?)null;
                codes.Add(new PlaceCode { Scheme = "bfs", Value = bfs, From = from ?? created, To = recodedTo.Key is null ? ended : recodedTo.Value.Date });
            }
            foreach (var hist in observations.GroupBy(o => o.Hist).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var starts = hist.Where(o => o.From && o.Date != HistoryStart).Select(o => (DateOnly?)o.Date).DefaultIfEmpty(null).Min();
                var ends = hist.Where(o => !o.From).Select(o => (DateOnly?)o.Date).DefaultIfEmpty(null).Max();
                codes.Add(new PlaceCode { Scheme = "bfs-hist", Value = hist.Key, From = starts, To = ends });
            }
            place.Codes = codes;
            place.TimeZone = SwitzerlandProfile.Zone;
            AddSource(place.Source, Bfs);
        }

        private string[] Languages(string? parent)
        {
            while (parent is not null)
            {
                if (cantonShortById.TryGetValue(parent, out var shortName))
                    return SwitzerlandProfile.CantonLanguages.GetValueOrDefault(shortName) ?? ["de"];
                parent = parentCanton.GetValueOrDefault(parent);
            }
            return ["de"];
        }

        // ---- streets and postal localities ----

        private void MapStreets()
        {
            foreach (var row in streets)
            {
                if (row["STR_STATUS"] == "planned" || row["STR_ESID"].Length == 0 || row["COM_FOSNR"].Length == 0)
                    continue;
                var municipality = CommuneId(row["COM_FOSNR"]);
                var languages = SwitzerlandProfile.CantonLanguages.GetValueOrDefault(row["COM_CANTON"]) ?? ["de"];
                var id = build.Ledger.Mint(Streets, "esid:" + row["STR_ESID"], RefIds.Street("ch", "esid", row["STR_ESID"]));
                var street = build.Street(id, "ch");
                if (street.Names.Entries.Count == 0)
                {
                    street.Names = SplitNames(row["STN_LABEL"], languages, Streets, row["STR_OFFICIAL"] == "false" ? TranslatedStringKind.Common : TranslatedStringKind.Official);
                    street.Type = row["STR_TYPE"] == "Area" ? StreetType.Hamlet : StreetType.Streetname;
                    street.Codes = [new PlaceCode { Scheme = "esid", Value = row["STR_ESID"] }];
                    AddSource(street.Source, Streets);
                }
                foreach (var label in row["ZIP_LABEL"].Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (ZipLabel().Match(label) is not { Success: true } match)
                    {
                        build.Warnings.Add($"{Streets}: street {row["STR_ESID"]} has an unreadable postal label '{label}'.");
                        continue;
                    }
                    var code = match.Groups["code"].Value;
                    var postalId = build.Ledger.Mint(Streets, "postcode:" + code, RefIds.PostalCode("ch", code));
                    var postal = build.PostalCode(postalId, "ch", code);
                    Locality(postal, match.Groups["name"].Value, languages[0], Streets, municipality);
                    StreetIn(street, municipality, postalId);
                }
            }
        }

        /// <summary>A BFS or swisstopo name with one part per official language ("Bern / Berne", "Bezirk See / District du Lac").</summary>
        private static TranslatedString SplitNames(string value, string[] languages, string source, TranslatedStringKind kind)
        {
            var parts = value.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts.Length > languages.Length)
                return new TranslatedString { Entries = [Name(languages[0], value, kind, source)] };
            var entries = new List<TranslatedStringEntry>();
            var free = languages.ToList();
            foreach (var part in parts)
            {
                var lang = LanguageOf(part) is { } l && free.Contains(l) ? l : free[0];
                free.Remove(lang);
                entries.Add(Name(lang, part, kind, source));
            }
            return new TranslatedString { Entries = entries };
        }

        /// <summary>The language a district name's generic word gives away ("Bezirk See / District du Lac").</summary>
        private static string? LanguageOf(string part) => part.Split(' ')[0] switch
        {
            "Bezirk" or "Verwaltungskreis" or "Wahlkreis" or "Kanton" => "de",
            "District" or "Arrondissement" or "Canton" => "fr",
            "Distretto" or "Cantone" => "it",
            "Regiun" => "rm",
            _ => null,
        };

        /// <summary>The register dates units founded in 1848 (or present in 1960) to 1848; history is known since 1960.</summary>
        private static DateOnly? Since(DateOnly? from) => from is { } d && d > HistoryStart ? d : null;
    }
}
