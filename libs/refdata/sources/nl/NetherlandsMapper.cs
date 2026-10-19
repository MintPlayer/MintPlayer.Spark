using System.Text;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Netherlands;

/// <summary>
/// The Dutch mapper (PRD §4.4, plan R8): CBS provinces and gemeenten with their herindelingen since 1991, and the BAG's
/// woonplaatsen, streets (openbare ruimten of type <c>Weg</c>) and postcodes.
/// <list type="bullet">
/// <item><b>Gemeenten</b> are keyed on the CBS code (<c>GM1979</c>); a herindeling under a new code is a <b>merge</b>
/// (Eemsdelta 2021 = Appingedam + Delfzijl + Loppersum), one where a participant keeps its code an <b>absorb</b>
/// (Groningen 2019 with Haren and Ten Boer). CBS lists a continuing gemeente as both ended and new.</item>
/// <item>An ended gemeente's province comes from the herindeling table's own abbreviation, never from code digits.</item>
/// <item><b>Woonplaatsen</b> hang under their gemeente over time through Kadaster's gemeente–woonplaats relation file,
/// so a woonplaats of Appingedam lies in GM0003 until 2021 and in GM1979 after. Adjacent relation periods with the
/// same gemeente (the 1-day registration slivers of 2010) are joined.</item>
/// <item><b>Streets</b> follow their woonplaats's gemeente periods; postcodes (PC6) come from the nummeraanduidingen,
/// streamed, and no house number is kept (RD5). Only the current state of each nummeraanduiding counts.</item>
/// <item>BAG ids are 16 digits whose first four are the issuing gemeente's code at the time; they survive mergers
/// (Appingedam's streets keep <c>0003…</c> under Eemsdelta), so they are never read as hierarchy.</item>
/// </list>
/// </summary>
internal sealed class NetherlandsMapper : IReferenceDataMapper
{
    private const string Bag = NetherlandsBagSource.SourceId;
    private const string Cbs = NetherlandsCbsGemeentenSource.SourceId;
    private const string Herindelingen = NetherlandsHerindelingenSource.SourceId;

    /// <summary>The province abbreviations of the CBS herindeling tables → CBS province codes.</summary>
    private static readonly Dictionary<string, string> Provinces = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Gr"] = "PV20", ["Fr"] = "PV21", ["Fryslân"] = "PV21", ["Dr"] = "PV22", ["Ov"] = "PV23", ["Fl"] = "PV24", ["Gld"] = "PV25", ["Gl"] = "PV25",
        ["Utr"] = "PV26", ["Ut"] = "PV26", ["NH"] = "PV27", ["N-H"] = "PV27", ["ZH"] = "PV28", ["Z-H"] = "PV28", ["Zld"] = "PV29", ["Zl"] = "PV29",
        ["NB"] = "PV30", ["N-B"] = "PV30", ["L"] = "PV31", ["Lb"] = "PV31",
    };

    public IReadOnlyList<string> Countries => ["nl"];

    public IReadOnlyList<string> SourceIds => [Cbs, Herindelingen, Bag];

    public int Order => 10;

    public bool IsCountryAdapter => true;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(NetherlandsMapper).Assembly);

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        new Run(build, inputs).Map();
        return Task.CompletedTask;
    }

    private sealed class Run(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs)
    {
        private string country = string.Empty;

        public void Map()
        {
            if (inputs.Count == 0) return;
            var primary = new[] { Cbs, Herindelingen, Bag }.First(inputs.ContainsKey);
            Country(build, "nl", "NLD", "528", NetherlandsProfile.Create(), primary);
            country = CountryPlace(build, "nl", primary, NetherlandsProfile.Zone, "nl");
            MapGemeenten();
            MapWoonplaatsenAndStreets();
        }

        private string Gemeente(string code) => build.Ledger.Mint(Cbs, "cbs:" + code, RefIds.Place("nl", "cbs", code));

        private string Province(string code) => build.Ledger.Mint(Cbs, "cbs:" + code, RefIds.Place("nl", "cbs", code));

        // ---- CBS ----

        private void MapGemeenten()
        {
            var current = inputs.TryGetValue(Cbs, out var cbs)
                ? cbs.Files.SelectMany(f => NetherlandsReaders.Gemeenten(f.Open())).ToList()
                : [];
            var herindelingen = new List<Herindeling>();
            if (inputs.TryGetValue(Herindelingen, out var her))
                foreach (var file in her.Files.OrderBy(f => f.Name, StringComparer.Ordinal))
                    if (NetherlandsHerindelingenSource.File().Match(file.Name) is { Success: true } m)
                    {
                        using var reader = new StreamReader(file.Open(), Encoding.UTF8);
                        herindelingen.AddRange(NetherlandsReaders.Herindelingen(reader.ReadToEnd(), int.Parse(m.Groups["year"].Value, System.Globalization.CultureInfo.InvariantCulture)));
                    }

            foreach (var province in current.GroupBy(g => g.ProvinceCode).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var place = build.Place(Province(province.Key));
                place.Periods = [new PlacePeriod { Kind = "nl:province", Role = PlaceRole.Region, Parent = country }];
                place.Names = new TranslatedString { Entries = [Name("nl", province.First().ProvinceName, TranslatedStringKind.Official, Cbs)] };
                Common(place, "cbs", province.Key, Cbs);
            }

            // Start and end of every gemeente the herindelingen name: a new code starts on 1 January of the year, an
            // ended one (listed as opgeheven under another code) ends that day.
            var start = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
            var end = new Dictionary<string, (DateOnly Date, Herindeling Row)>(StringComparer.Ordinal);
            foreach (var row in herindelingen.OrderBy(r => r.Year))
            {
                var date = new DateOnly(row.Year, 1, 1);
                if (row.OldCode != row.NewCode)
                    end[row.OldCode] = (date, row);
                if (!herindelingen.Any(r => r.Year == row.Year && r.OldCode == row.NewCode))
                    start.TryAdd(row.NewCode, date);
            }

            foreach (var gemeente in current.OrderBy(g => g.Code, StringComparer.Ordinal))
            {
                var code = gemeente.Code[2..];
                var place = build.Place(Gemeente(gemeente.Code));
                place.Periods = [new PlacePeriod { From = start.TryGetValue(code, out var from) ? from : null, Kind = "nl:municipality", Role = PlaceRole.Municipality, Parent = Province(gemeente.ProvinceCode) }];
                place.Names = new TranslatedString { Entries = [Name("nl", gemeente.Name, TranslatedStringKind.Official, Cbs)] };
                Common(place, "cbs", gemeente.Code, Cbs);
            }

            // Ended gemeenten: from the herindeling tables only.
            foreach (var (code, (date, row)) in end.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (current.Any(g => g.Code == "GM" + code))
                {
                    build.Warnings.Add($"{Herindelingen}: GM{code} ended on {date:yyyy-MM-dd}, but CBS lists it as current: kept current.");
                    continue;
                }
                var place = build.Place(Gemeente("GM" + code));
                var province = Provinces.TryGetValue(row.Province, out var pv) && build.Places.ContainsKey(Province(pv)) ? Province(pv) : null;
                if (province is null)
                    build.Warnings.Add($"{Herindelingen}: the province '{row.Province}' of the ended GM{code} is unknown: no parent.");
                place.Periods = [new PlacePeriod { From = start.TryGetValue(code, out var from) ? from : null, To = date, Kind = "nl:municipality", Role = PlaceRole.Municipality, Parent = province }];
                place.Names = new TranslatedString { Entries = [Name("nl", row.OldName, TranslatedStringKind.Official, Herindelingen, to: date)] };
                Common(place, "cbs", "GM" + code, Herindelingen);
                place.Codes[0].To = date;
            }

            // Lineage: one event per (year, receiving gemeente).
            foreach (var group in herindelingen.GroupBy(r => (r.Year, r.NewCode)).OrderBy(g => g.Key.Year).ThenBy(g => g.Key.NewCode, StringComparer.Ordinal))
            {
                var date = new DateOnly(group.Key.Year, 1, 1);
                var receiver = Gemeente("GM" + group.Key.NewCode);
                var absorb = group.Any(r => r.OldCode == r.NewCode);
                var kind = absorb ? LineageKind.Absorb : LineageKind.Merge;
                var lineage = new RefLineageEvent
                {
                    Id = RefIds.LineageEvent("nl", date, kind, "cbs", "GM" + group.Key.NewCode),
                    Country = "nl",
                    Date = date,
                    Kind = kind,
                    Operations = [new LineageOperation { Place = receiver, Op = absorb ? LineageOp.Continue : LineageOp.Create }],
                    Sources = [new LineageSource { Source = Herindelingen, Date = date, Note = $"CBS: {string.Join(", ", group.Where(r => r.OldCode != r.NewCode).Select(r => $"{r.OldCode} {r.OldName}"))} → {group.Key.NewCode} {group.First().NewName}" }],
                };
                foreach (var row in group.Where(r => r.OldCode != r.NewCode).OrderBy(r => r.OldCode, StringComparer.Ordinal))
                    lineage.Operations.Add(new LineageOperation { Place = Gemeente("GM" + row.OldCode), Op = LineageOp.End, Into = receiver, Share = SuccessionShare.Whole });
                build.AddLineage(lineage);
            }
        }

        // ---- BAG ----

        private void MapWoonplaatsenAndStreets()
        {
            if (!inputs.TryGetValue(Bag, out var bag))
                return;
            var files = bag.Files.Where(f => NetherlandsBagSource.XmlFile().IsMatch(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
            IEnumerable<SourceFile> Of(string kind) => files.Where(f => f.Name.Contains(kind, StringComparison.Ordinal));

            // Gemeente periods per woonplaats (joined where adjacent and equal).
            var relations = Of("GEM-WPL-RELATIE").SelectMany(f => NetherlandsReaders.Relations(f.Open()))
                .GroupBy(r => r.Woonplaats)
                .ToDictionary(g => g.Key, g => Join(g.OrderBy(r => DateKeys.From(r.From)).ToList()), StringComparer.Ordinal);

            // Woonplaatsen.
            var woonplaatsen = new Dictionary<string, (string Id, List<(DateOnly? From, DateOnly? To, string Gemeente)> Periods, string Name)>(StringComparer.Ordinal);
            foreach (var group in Of("9999WPL").SelectMany(f => NetherlandsReaders.BagObjects(f.Open(), "Woonplaats")).GroupBy(o => o.Id).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var states = group.Select(g => g.State).OrderBy(s => DateKeys.From(s.From)).ToList();
                var periods = relations.GetValueOrDefault(group.Key) ?? [];
                var known = periods.Where(p => build.Ledger.Find("cbs:GM" + p.Gemeente) is { } g && build.Places.ContainsKey(g)).ToList();
                if (known.Count == 0)
                {
                    build.Warnings.Add($"{Bag}: woonplaats {group.Key} {states[^1].Name} lies in no gemeente the build holds: skipped.");
                    continue;
                }
                var id = build.Ledger.Mint(Bag, "bag-wpl:" + group.Key, RefIds.Place("nl", "bag-wpl", group.Key));
                var place = build.Place(id);
                var last = states[^1];
                DateOnly? ended = last.Status.Contains("ingetrokken", StringComparison.OrdinalIgnoreCase) ? last.From : last.To;
                place.Periods = [.. known.Select(p => new PlacePeriod
                {
                    From = p.From, To = p.To ?? ended, Kind = "nl:woonplaats", Role = PlaceRole.Locality, Parent = Gemeente("GM" + p.Gemeente),
                })];
                place.Names = new TranslatedString();
                foreach (var state in states.Where(s => !s.Status.Contains("ingetrokken", StringComparison.OrdinalIgnoreCase)))
                {
                    var index = place.Names.Entries.FindIndex(e => e.Value == state.Name && e.ValidTo == state.From);
                    if (index >= 0)
                        place.Names.Entries[index] = place.Names.Entries[index] with { ValidTo = state.To };
                    else
                        place.Names.Entries.Add(Name("nl", state.Name, TranslatedStringKind.Official, Bag, state.From, state.To));
                }
                Common(place, "bag-wpl", group.Key, Bag);
                woonplaatsen[group.Key] = (id, [.. known.Select(p => (p.From, p.To ?? ended, Gemeente("GM" + p.Gemeente)))], last.Name);
            }

            // Streets: the openbare ruimten of type Weg.
            var streets = new Dictionary<string, (RefStreet Street, string Woonplaats)>(StringComparer.Ordinal);
            foreach (var group in Of("9999OPR").SelectMany(f => NetherlandsReaders.BagObjects(f.Open(), "OpenbareRuimte")).GroupBy(o => o.Id).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var states = group.Select(g => g.State).OrderBy(s => DateKeys.From(s.From)).ToList();
                var last = states[^1];
                if (last.Type != "Weg" || last.Woonplaats is null || !woonplaatsen.TryGetValue(last.Woonplaats, out var woonplaats))
                    continue;
                var id = build.Ledger.Mint(Bag, "bag-opr:" + group.Key, RefIds.Street("nl", "bag", group.Key));
                var street = build.Street(id, "nl");
                street.Names = new TranslatedString();
                foreach (var state in states.Where(s => s.Name.Length > 0 && !s.Status.Contains("ingetrokken", StringComparison.OrdinalIgnoreCase)))
                {
                    var index = street.Names.Entries.FindIndex(e => e.Value == state.Name && e.ValidTo == state.From);
                    if (index >= 0)
                        street.Names.Entries[index] = street.Names.Entries[index] with { ValidTo = state.To };
                    else
                        street.Names.Entries.Add(Name("nl", state.Name, TranslatedStringKind.Official, Bag, state.From, state.To));
                }
                street.Type = StreetType.Streetname;
                street.Codes = [new PlaceCode { Scheme = "bag-opr", Value = group.Key }];
                street.Source = [Bag];
                var born = states[0].From;
                DateOnly? ended = last.Status.Contains("ingetrokken", StringComparison.OrdinalIgnoreCase) ? last.From : last.To;
                // One period per gemeente the woonplaats lay in while the street existed.
                foreach (var (from, to, gemeente) in woonplaats.Periods)
                {
                    var f = Later(from, born);
                    var t = Earlier(to, ended);
                    if (f is { } a && t is { } b && a >= b) continue;
                    StreetIn(street, gemeente, null, woonplaats.Id, f, t);
                }
                streets[group.Key] = (street, last.Woonplaats);
            }

            // Postcodes from the current nummeraanduidingen (no house numbers kept, RD5).
            foreach (var (_, state) in Of("9999NUM").SelectMany(f => NetherlandsReaders.BagObjects(f.Open(), "Nummeraanduiding")))
            {
                if (state.To is not null || state.Postcode is not { Length: 6 } code || state.OpenbareRuimte is null)
                    continue;
                if (state.Status.Contains("ingetrokken", StringComparison.OrdinalIgnoreCase) || !streets.TryGetValue(state.OpenbareRuimte, out var street))
                    continue;
                var woonplaatsCode = state.Woonplaats ?? street.Woonplaats;
                if (!woonplaatsen.TryGetValue(woonplaatsCode, out var woonplaats))
                    continue;
                var postalId = build.Ledger.Mint(Bag, "postcode:" + code, RefIds.PostalCode("nl", code));
                var postal = build.PostalCode(postalId, "nl", code);
                var current = woonplaats.Periods.FirstOrDefault(p => p.To is null);
                var municipality = current.Gemeente ?? woonplaats.Periods[^1].Gemeente;
                Locality(postal, woonplaats.Name, "nl", Bag, municipality, woonplaats.Id, main: true, from: current.From);
                var period = street.Street.Periods.FirstOrDefault(p => p.To is null) ?? street.Street.Periods.LastOrDefault();
                if (period is not null && !period.PostalCodes.Contains(postalId, StringComparer.OrdinalIgnoreCase))
                    period.PostalCodes.Add(postalId);
            }
        }

        private static List<(DateOnly? From, DateOnly? To, string Gemeente)> Join(List<GemeenteWoonplaats> relations)
        {
            var joined = new List<(DateOnly? From, DateOnly? To, string Gemeente)>();
            foreach (var r in relations)
            {
                if (joined.Count > 0 && joined[^1].Gemeente == r.Gemeente && joined[^1].To == r.From)
                    joined[^1] = (joined[^1].From, r.To, r.Gemeente);
                else
                    joined.Add((r.From, r.To, r.Gemeente));
            }
            return joined;
        }

        private static DateOnly? Later(DateOnly? a, DateOnly? b) => a is null ? b : b is null ? a : a > b ? a : b;

        private static DateOnly? Earlier(DateOnly? a, DateOnly? b) => a is null ? b : b is null ? a : a < b ? a : b;

        private static void Common(RefPlace place, string scheme, string code, string source)
        {
            place.OfficialLanguages = [new() { Lang = "nl" }];
            place.Codes = [new PlaceCode { Scheme = scheme, Value = code }];
            place.TimeZone = NetherlandsProfile.Zone;
            place.Source = [source];
        }
    }
}
