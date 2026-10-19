using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Austria;

/// <summary>
/// Austria (PRD §4.4, plan R8). Bundesländer and political districts from Statistik Austria's regional lists;
/// Gemeinden (GKZ), Ortschaften (OKZ), streets (SKZ) and postcodes from BEV's Adressregister, the postcode of each
/// address giving the street ↔ postcode links (verified on the 2026-10-01 tables: every address carries a PLZ); no house
/// numbers are kept (RD5). History since 2002 from Statistik Austria's change file:
/// <list type="bullet">
/// <item><b>Recode</b> (Ebergassing 32401 → 30729 when Wien-Umgebung was dissolved, 2017): the GKZ embeds the district,
/// so a district merger re-keys its Gemeinden. The same place gets the new code and a new parent; the id keeps the code
/// that minted it.</item>
/// <item><b>Merge</b> (Söchau 62252 + Fürstenfeld 62267 → Fürstenfeld 62280, 2025): a new GKZ; the predecessors end.</item>
/// <item><b>Split</b> (Murfeld 62347, 2020: its KG Seibersdorf to Sankt Veit in der Südsteiermark 61060, the rest to
/// Straß in Steiermark 61061): the old Gemeinde ends with <c>part</c> edges to each successor.</item>
/// <item><b>Rename</b> (Lenzing → Lenzing an der Ager, 2024-11-28): dated names.</item>
/// </list>
/// Hierarchy: a Gemeinde's district is the first three digits of the GKZ valid in a period (Austria's own code rule;
/// the change file records every re-keying). Ortschaften hang off their current Gemeinde: BEV publishes no Ortschaft
/// history. Retired districts (Wien-Umgebung, Radkersburg, Fürstenfeld) are places that end on their merger date.
/// </summary>
internal sealed partial class AustriaMapper : IReferenceDataMapper
{
    private const string Bev = AustriaAddressRegisterSource.SourceId;
    private const string Regions = AustriaRegionsSource.SourceId;
    private const string Changes = AustriaChangesSource.SourceId;

    [GeneratedRegex(@"^(?<table>GEMEINDE|ORTSCHAFT|STRASSE|ADRESSE)(\.cut)?\.csv$")]
    private static partial Regex TableFile();

    public IReadOnlyList<string> Countries => ["at"];
    public IReadOnlyList<string> SourceIds => [Bev, Regions, Changes];
    public int Order => 10;
    public bool IsCountryAdapter => true;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(AustriaMapper).Assembly);

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        var run = new Run(build);
        if (inputs.TryGetValue(Regions, out var regions) && regions.Find(AustriaRegionsSource.Entries()) is { } pb)
            using (var stream = pb.Open())
                run.ReadDistricts(RefCsv.Read(stream, ';'));
        if (inputs.TryGetValue(Changes, out var changes) && changes.Find(AustriaChangesSource.Entries()) is { } ods)
            using (var stream = ods.Open())
                run.ReadChanges(OdsReader.Read(stream));
        if (inputs.TryGetValue(Bev, out var bev))
            foreach (var file in bev.Files.Where(f => TableFile().IsMatch(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal))
                using (var stream = file.Open())
                    run.ReadTable(TableFile().Match(file.Name).Groups["table"].Value, RefCsv.ReadRecords(stream, ';'));
        run.Map();
        return Task.CompletedTask;
    }

    private sealed record Recode(string New, string Old, DateOnly Date, string NewDistrict, string OldDistrict);
    private sealed record Link(string New, string NewName, string Old, string OldName, DateOnly Date);
    private sealed record Rename(string Code, string OldName, string NewName, DateOnly Date);

    private sealed class Run(RefDataBuild build)
    {
        private readonly Dictionary<string, string> states = new(StringComparer.Ordinal);          // "1" → name
        private readonly Dictionary<string, string> districts = new(StringComparer.Ordinal);       // "101" → name (current)
        private readonly Dictionary<string, string> retiredDistricts = new(StringComparer.Ordinal); // "324" → name
        private readonly Dictionary<string, DateOnly> districtEnds = new(StringComparer.Ordinal);
        private readonly List<Recode> recodes = [];
        private readonly List<Link> merges = [];
        private readonly List<Link> splits = [];
        private readonly List<Rename> renames = [];
        private readonly Dictionary<string, List<string>> sheetNames = new(StringComparer.Ordinal);

        private readonly Dictionary<string, string> gemeinden = new(StringComparer.Ordinal);       // GKZ → name
        private readonly Dictionary<string, (string Gkz, string Name)> ortschaften = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Name, string Addition, string Gkz)> strassen = new(StringComparer.Ordinal);
        private readonly HashSet<(string Skz, string Gkz, string Okz, string Plz)> addresses = [];

        private readonly Dictionary<string, Recode> recodedFrom = new(StringComparer.Ordinal);
        private string countryPlace = string.Empty;

        // ---- reading ----

        public void ReadDistricts(IEnumerable<string[]> rows)
        {
            var header = false;
            foreach (var f in rows)
            {
                if (!header) { header = f.Length > 0 && f[0].Trim() == "Bundeslandkennziffer"; continue; }
                if (f.Length < 4 || f[0].Trim().Length == 0 || f[2].Trim().Length != 3) continue;
                states[f[0].Trim()] = f[1].Trim();
                districts[f[2].Trim()] = f[3].Trim();
            }
        }

        public void ReadChanges(Dictionary<string, List<string[]>> sheets)
        {
            foreach (var (name, rows) in sheets)
            {
                var headerIndex = rows.FindIndex(r => r.Length > 0 && r[0].StartsWith("Gemeindekennziffer", StringComparison.Ordinal));
                if (headerIndex < 0) continue;
                var h = rows[headerIndex];
                int Col(params string[] names) => Array.FindIndex(h, c => names.Any(n => c.Trim().Equals(n, StringComparison.OrdinalIgnoreCase)));
                string At(string[] r, int i) => i >= 0 && i < r.Length ? r[i].Trim() : string.Empty;
                foreach (var r in rows.Skip(headerIndex + 1))
                {
                    var date = Date(At(r, Col("in Kraft seit")), "dd.MM.yyyy");
                    if (date is not { } day) continue;
                    switch (name)
                    {
                        case "Zusammenlegungen" or "Teilungen":
                            var link = new Link(At(r, Col("Gemeindekennziffer neu")), At(r, Col("Gemeindename neu")), At(r, Col("Gemeindekennziffer alt")), At(r, Col("Gemeindename alt")), day);
                            if (link.New.Length != 5 || link.Old.Length != 5) continue;
                            (name == "Teilungen" ? splits : merges).Add(link);
                            Named(link.New, link.NewName);
                            Named(link.Old, link.OldName);
                            break;
                        case "Bezirkszusammenlegungen":
                            var recode = new Recode(At(r, Col("Gemeindekennziffer neu")), At(r, Col("Gemeindekennziffer alt")), day, At(r, Col("Politischer Bezirk neu")), At(r, Col("Politischer Bezirk alt")));
                            if (recode.New.Length != 5 || recode.Old.Length != 5) continue;
                            recodes.Add(recode);
                            Named(recode.New, At(r, Col("Gemeindename")));
                            Named(recode.Old, At(r, Col("Gemeindename")));
                            retiredDistricts.TryAdd(recode.Old[..3], recode.OldDistrict);
                            retiredDistricts.TryAdd(recode.New[..3], recode.NewDistrict);
                            if (!districtEnds.TryGetValue(recode.Old[..3], out var end) || day < end)
                                districtEnds[recode.Old[..3]] = day;
                            break;
                        case "Namensaenderungen":
                            var rename = new Rename(At(r, Col("Gemeindekennziffer")), At(r, Col("Gemeindename alt")), At(r, Col("Gemeindename neu")), day);
                            if (rename.Code.Length == 5) renames.Add(rename);
                            break;
                    }
                }
            }
        }

        private void Named(string code, string name)
        {
            if (code.Length != 5 || name.Length == 0) return;
            if (!sheetNames.TryGetValue(code, out var list)) sheetNames[code] = list = [];
            list.Add(name);
        }

        public void ReadTable(string table, IEnumerable<RefCsvRow> rows)
        {
            foreach (var r in rows)
                switch (table)
                {
                    case "GEMEINDE": gemeinden[r["GKZ"]] = r["GEMEINDENAME"]; break;
                    case "ORTSCHAFT": ortschaften[r["OKZ"]] = (r["GKZ"], r["ORTSNAME"]); break;
                    case "STRASSE": strassen[r["SKZ"]] = (r["STRASSENNAME"], r["STRASSENNAMENZUSATZ"], r["GKZ"]); break;
                    case "ADRESSE": if (r["PLZ"].Length > 0 && r["SKZ"].Length > 0) addresses.Add((r["SKZ"], r["GKZ"], r["OKZ"], r["PLZ"])); break;
                }
        }

        // ---- mapping ----

        public void Map()
        {
            countryPlace = CountryPlace(build, "at", Bev, AustriaProfile.Zone, "de");
            Country(build, "at", "AUT", "040", AustriaProfile.Create(), Regions);
            foreach (var recode in recodes)
                recodedFrom[recode.New] = recode;
            foreach (var (code, name) in states.OrderBy(s => s.Key, StringComparer.Ordinal))
                MapState(code, name);
            foreach (var code in districts.Keys.Concat(retiredDistricts.Keys).Distinct().Order(StringComparer.Ordinal))
                District(code);
            MapGemeinden();
            MapOrtschaftenAndStreets();
        }

        private void MapState(string code, string name)
        {
            var id = build.Ledger.Mint(Regions, "bundesland:" + code, RefIds.Place("at", "bundesland", code));
            var place = build.Place(id);
            place.Periods = [new PlacePeriod { Kind = "at:state", Role = PlaceRole.Region, Parent = countryPlace }];
            place.Names = new TranslatedString { Entries = [Name("de", name, TranslatedStringKind.Official, Regions)] };
            place.OfficialLanguages = [new() { Lang = "de" }];
            place.Codes = [new PlaceCode { Scheme = "bundesland", Value = code }, new PlaceCode { Scheme = "iso3166-2", Value = "AT-" + code }];
            place.TimeZone = AustriaProfile.Zone;
            AddSource(place.Source, Regions);
        }

        private string State(string code) => build.Ledger.Mint(Regions, "bundesland:" + code, RefIds.Place("at", "bundesland", code));

        /// <summary>A political district by its three-digit code: current from the regional list, retired from the change file.</summary>
        private string District(string code)
        {
            var id = build.Ledger.Mint(Regions, "bezirk:" + code, RefIds.Place("at", "bezirk", code));
            var place = build.Place(id);
            if (place.Periods.Count > 0)
                return id;
            var current = districts.TryGetValue(code, out var name);
            name ??= retiredDistricts.GetValueOrDefault(code) ?? code;
            var to = current ? null : districtEnds.TryGetValue(code, out var end) ? end : (DateOnly?)null;
            place.Periods = [new PlacePeriod { To = to, Kind = "at:district", Role = PlaceRole.Subregion, Parent = State(code[..1]) }];
            place.Names = new TranslatedString { Entries = [Name("de", name, to is null ? TranslatedStringKind.Official : TranslatedStringKind.Historic, current ? Regions : Changes)] };
            place.OfficialLanguages = [new() { Lang = "de" }];
            place.Codes = [new PlaceCode { Scheme = "bezirk", Value = code, To = to }];
            place.TimeZone = AustriaProfile.Zone;
            AddSource(place.Source, current ? Regions : Changes);
            return id;
        }

        private string GemeindeId(string gkz)
        {
            if (recodedFrom.TryGetValue(gkz, out var recode))
                return build.Ledger.Alias(Changes, "gkz:" + gkz, GemeindeId(recode.Old));
            return build.Ledger.Mint(Bev, "gkz:" + gkz, RefIds.Place("at", "gkz", gkz));
        }

        /// <summary>The codes a Gemeinde held, oldest first, with the date each started (null: before 2002).</summary>
        private List<(string Code, DateOnly? From)> Chain(string gkz)
        {
            var chain = new List<(string, DateOnly?)>();
            var code = gkz;
            DateOnly? from = null;
            while (recodedFrom.TryGetValue(code, out var recode))
            {
                chain.Insert(0, (code, recode.Date));
                code = recode.Old;
            }
            chain.Insert(0, (code, from));
            return chain;
        }

        private void MapGemeinden()
        {
            // Every code the tables and the change file know, by place.
            var codes = gemeinden.Keys
                .Concat(merges.SelectMany(m => new[] { m.New, m.Old })).Concat(splits.SelectMany(s => new[] { s.New, s.Old }))
                .Concat(recodes.SelectMany(r => new[] { r.New, r.Old })).Concat(renames.Select(r => r.Code))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            // The newest code of each place (the end of its recode chain).
            var newest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in codes.Where(c => !recodes.Any(r => r.Old == c)))
                newest[GemeindeId(code)] = code;

            var created = new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase);
            var ended = new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in merges.Concat(splits))
            {
                var successor = GemeindeId(link.New);
                var predecessor = GemeindeId(link.Old);
                if (string.Equals(successor, predecessor, StringComparison.OrdinalIgnoreCase)) continue;
                created[successor] = link.Date;
                ended[predecessor] = link.Date;
            }

            foreach (var (id, code) in newest.OrderBy(n => n.Key, StringComparer.Ordinal))
                MapGemeinde(id, code, created.TryGetValue(id, out var c) ? c : null, ended.TryGetValue(id, out var e) ? e : null);

            MapLineage();
        }

        private void MapGemeinde(string id, string newestCode, DateOnly? created, DateOnly? ended)
        {
            var place = build.Place(id);
            var chain = Chain(newestCode);
            place.Periods = [];
            place.Codes = [];
            for (var i = 0; i < chain.Count; i++)
            {
                var from = i == 0 ? created : chain[i].From;
                var to = i + 1 < chain.Count ? chain[i + 1].From : ended;
                place.Periods.Add(new PlacePeriod { From = from, To = to, Kind = "at:municipality", Role = PlaceRole.Municipality, Parent = District(chain[i].Code[..3]) });
                place.Codes.Add(new PlaceCode { Scheme = "gkz", Value = chain[i].Code, From = from, To = to });
            }

            // Names: the register's current name, else the change file's (an annotated "Murfeld (ohne KG …)" only when nothing plainer exists).
            var name = gemeinden.GetValueOrDefault(newestCode)
                ?? chain.Select(c => sheetNames.GetValueOrDefault(c.Code)).OfType<List<string>>().SelectMany(n => n)
                    .OrderBy(n => n.Contains('(') ? 1 : 0).ThenBy(n => n.Length).FirstOrDefault()
                ?? newestCode;
            var kind = ended is null ? TranslatedStringKind.Official : TranslatedStringKind.Historic;
            var renamed = renames.Where(r => chain.Any(c => c.Code == r.Code)).OrderBy(r => r.Date).ToList();
            var entries = new List<TranslatedStringEntry>();
            DateOnly? since = null;
            foreach (var rename in renamed)
            {
                entries.Add(Name("de", rename.OldName, TranslatedStringKind.Historic, Changes, since, rename.Date));
                since = rename.Date;
            }
            entries.Add(Name("de", renamed.Count > 0 ? renamed[^1].NewName : name, kind, gemeinden.ContainsKey(newestCode) ? Bev : Changes, since, null));
            place.Names = new TranslatedString { Entries = entries };
            place.OfficialLanguages = [new() { Lang = "de" }];
            place.TimeZone = AustriaProfile.Zone;
            AddSource(place.Source, gemeinden.ContainsKey(newestCode) ? Bev : Changes);
        }

        private void MapLineage()
        {
            // Splits first: a split Gemeinde ends with part edges to each successor; the mergers that received its parts
            // then name only their other predecessors.
            var splitOn = new HashSet<(string Old, DateOnly Date)>();
            foreach (var group in splits.GroupBy(s => (s.Old, s.Date)).OrderBy(g => g.Key.Date).ThenBy(g => g.Key.Old, StringComparer.Ordinal))
            {
                splitOn.Add(group.Key);
                var predecessor = GemeindeId(group.Key.Old);
                var lineage = new RefLineageEvent
                {
                    Id = RefIds.LineageEvent("at", group.Key.Date, LineageKind.Split, "gkz", group.Key.Old),
                    Country = "at", Date = group.Key.Date, Kind = LineageKind.Split,
                    Sources = [new LineageSource { Source = Changes, Date = group.Key.Date, Note = "Teilungen: " + string.Join(", ", group.Select(s => s.New).Distinct().Order()) }],
                };
                foreach (var successor in group.Select(s => s.New).Distinct().Order(StringComparer.Ordinal))
                {
                    lineage.Operations.Add(new LineageOperation { Place = predecessor, Op = LineageOp.End, Into = GemeindeId(successor), Share = SuccessionShare.Part });
                    if (!merges.Any(m => m.New == successor && m.Date == group.Key.Date))
                        lineage.Operations.Add(new LineageOperation { Place = GemeindeId(successor), Op = LineageOp.Create });
                }
                build.AddLineage(lineage);
            }

            foreach (var group in merges.GroupBy(m => (m.New, m.Date)).OrderBy(g => g.Key.Date).ThenBy(g => g.Key.New, StringComparer.Ordinal))
            {
                var successor = GemeindeId(group.Key.New);
                var lineage = new RefLineageEvent
                {
                    Id = RefIds.LineageEvent("at", group.Key.Date, LineageKind.Merge, "gkz", group.Key.New),
                    Country = "at", Date = group.Key.Date, Kind = LineageKind.Merge,
                    Operations = [new LineageOperation { Place = successor, Op = LineageOp.Create }],
                    Sources = [new LineageSource { Source = Changes, Date = group.Key.Date, Note = "Zusammenlegungen: " + string.Join(", ", group.Select(m => m.Old).Distinct().Order()) + " → " + group.Key.New }],
                };
                foreach (var old in group.Select(m => m.Old).Distinct().Order(StringComparer.Ordinal))
                    if (!splitOn.Contains((old, group.Key.Date)) && !string.Equals(GemeindeId(old), successor, StringComparison.OrdinalIgnoreCase))
                        lineage.Operations.Add(new LineageOperation { Place = GemeindeId(old), Op = LineageOp.End, Into = successor, Share = SuccessionShare.Whole });
                build.AddLineage(lineage);
            }

            foreach (var recode in recodes.OrderBy(r => r.Date).ThenBy(r => r.New, StringComparer.Ordinal))
            {
                var place = GemeindeId(recode.New);
                var lineage = new RefLineageEvent
                {
                    Id = RefIds.LineageEvent("at", recode.Date, LineageKind.Recode, "gkz", recode.New),
                    Country = "at", Date = recode.Date, Kind = LineageKind.Recode,
                    Operations = [new LineageOperation { Place = place, Op = LineageOp.Recode }],
                    Sources = [new LineageSource { Source = Changes, Date = recode.Date, Note = $"Bezirkszusammenlegung: {recode.Old} ({recode.OldDistrict}) → {recode.New} ({recode.NewDistrict})" }],
                };
                if (recode.Old[..3] != recode.New[..3])
                    lineage.Operations.Add(new LineageOperation { Place = place, Op = LineageOp.Reparent });
                build.AddLineage(lineage);
            }

            foreach (var rename in renames.OrderBy(r => r.Date).ThenBy(r => r.Code, StringComparer.Ordinal))
                build.AddLineage(new RefLineageEvent
                {
                    Id = RefIds.LineageEvent("at", rename.Date, LineageKind.Rename, "gkz", rename.Code),
                    Country = "at", Date = rename.Date, Kind = LineageKind.Rename,
                    Operations = [new LineageOperation { Place = GemeindeId(rename.Code), Op = LineageOp.Rename }],
                    Sources = [new LineageSource { Source = Changes, Date = rename.Date, Note = $"{rename.OldName} → {rename.NewName}" }],
                });
        }

        private void MapOrtschaftenAndStreets()
        {
            foreach (var (okz, (gkz, name)) in ortschaften.OrderBy(o => o.Key, StringComparer.Ordinal))
            {
                var id = build.Ledger.Mint(Bev, "okz:" + okz, RefIds.Place("at", "okz", okz));
                var place = build.Place(id);
                place.Periods = [new PlacePeriod { Kind = "at:locality", Role = PlaceRole.Locality, Parent = GemeindeId(gkz) }];
                place.Names = new TranslatedString { Entries = [Name("de", name, TranslatedStringKind.Official, Bev)] };
                place.OfficialLanguages = [new() { Lang = "de" }];
                place.Codes = [new PlaceCode { Scheme = "okz", Value = okz }];
                place.TimeZone = AustriaProfile.Zone;
                AddSource(place.Source, Bev);
            }

            foreach (var (skz, (name, addition, gkz)) in strassen.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                var street = Street(skz, name, addition);
                StreetIn(street, GemeindeId(gkz), null);
            }

            foreach (var (skz, gkz, okz, plz) in addresses.OrderBy(a => a.Skz, StringComparer.Ordinal).ThenBy(a => a.Gkz, StringComparer.Ordinal).ThenBy(a => a.Okz, StringComparer.Ordinal).ThenBy(a => a.Plz, StringComparer.Ordinal))
            {
                var municipality = GemeindeId(gkz);
                var postalId = build.Ledger.Mint(Bev, "postcode:" + plz, RefIds.PostalCode("at", plz));
                var postal = build.PostalCode(postalId, "at", plz);
                var locality = ortschaften.TryGetValue(okz, out var o) ? o.Name : gemeinden.GetValueOrDefault(gkz) ?? plz;
                var localityPlace = ortschaften.ContainsKey(okz) ? build.Ledger.Find("okz:" + okz) : null;
                Locality(postal, locality, "de", Bev, municipality, localityPlace);
                if (strassen.TryGetValue(skz, out var s))
                    StreetIn(Street(skz, s.Name, s.Addition), municipality, postalId, localityPlace);
            }
        }

        private RefStreet Street(string skz, string name, string addition)
        {
            var id = build.Ledger.Mint(Bev, "skz:" + skz, RefIds.Street("at", "skz", skz));
            var street = build.Street(id, "at");
            if (street.Names.Entries.Count == 0)
            {
                street.Names = new TranslatedString { Entries = [Name("de", name, TranslatedStringKind.Official, Bev)] };
                street.Qualifier = addition.Length > 0 ? addition : null;
                street.Type = StreetType.Streetname;
                street.Codes = [new PlaceCode { Scheme = "skz", Value = skz }];
                AddSource(street.Source, Bev);
            }
            return street;
        }
    }
}
