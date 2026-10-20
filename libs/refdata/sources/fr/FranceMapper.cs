using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.France;

/// <summary>A row of <c>v_commune_depuis_1943</c>: one name of one code over one validity.</summary>
internal sealed record CogCommuneRow(string Code, string Name, DateOnly From, DateOnly? To);

/// <summary>A row of <c>v_mvt_commune</c>: one participant of one mouvement (MOD), before → after.</summary>
internal sealed record CogMovement(string Mod, DateOnly Date, string TypeBefore, string CodeBefore, string NameBefore, string TypeAfter, string CodeAfter, string NameAfter);

/// <summary>
/// The French mapper (PRD §4.4, plan R8): INSEE regions, départements and communes since 1943 with their mouvements,
/// BAN streets and postcodes, and La Poste's postal localities.
/// <list type="bullet">
/// <item><b>An INSEE code is not a territory id</b>: codes are reused by design. When a commune nouvelle takes the code of
/// a commune that carries on as its commune déléguée (MOD 32: <c>COM 01015 → COMD 01015</c> and <c>COM 01015</c>), the
/// code is <b>reissued</b>: the new commune gets <c>places/fr/insee/01015@2016-01-01</c> (Arboys en Bugey) and the plain
/// <c>places/fr/insee/01015</c> stays the commune déléguée Arbignieu (§4.3). A rétablissement (MOD 21) reuses the old place.</item>
/// <item>A code change (MOD 41 département change, MOD 50 chef-lieu transfer) is a <b>recode</b>: the id keeps the old
/// code (Ajaccio <c>20004</c> → <c>2A004</c> in 1976).</item>
/// <item>A commune that becomes déléguée (MOD 32) or associée (MOD 33) continues one level down (a demotion); MOD 34/35
/// end it into its commune.</item>
/// <item>The COG publishes départements of the current edition only. A commune's département is the current one while
/// its code is current, else the one its code names (an INSEE code embeds its département, and a département change
/// re-codes the commune, MOD 41); a département missing from the edition (Corse, <c>20</c>, before 1976) leaves the period
/// without a parent, with a warning.</item>
/// <item>Streets are keyed on the BAN toponym UUID (<c>id_ban_toponyme</c>) where the file carries it, never on
/// <c>cle_interop</c>; the national CSV of 2026-10-19 has no UUID column, so the fallback is the toponym key
/// (<c>id_fantoir</c>, else the first two parts of the address id), which embeds the commune code (see the README).</item>
/// </list>
/// </summary>
internal sealed partial class FranceMapper : IReferenceDataMapper
{
    private const string Cog = FranceCogSource.SourceId;
    private const string Ban = FranceBanSource.SourceId;
    private const string LaPoste = FranceLaPosteSource.SourceId;
    private static readonly DateOnly CogStart = new(1943, 1, 1);

    [GeneratedRegex(@"^v_region_\d{4}(\.cut)?\.csv$")] private static partial Regex RegionFile();
    [GeneratedRegex(@"^v_departement_\d{4}(\.cut)?\.csv$")] private static partial Regex DepartementFile();
    [GeneratedRegex(@"^v_commune_\d{4}(\.cut)?\.csv$")] private static partial Regex CommuneFile();
    [GeneratedRegex(@"^v_commune_depuis_1943(\.cut)?\.csv$")] private static partial Regex HistoryFile();
    [GeneratedRegex(@"^v_mvt_commune_\d{4}(\.cut)?\.csv$")] private static partial Regex MovementFile();
    [GeneratedRegex(@"^adresses-[0-9AB]{2,3}(\.cut)?\.csv$")] private static partial Regex BanFile();
    [GeneratedRegex(@"^laposte_hexasmal(\.cut)?\.csv$")] private static partial Regex LaPosteFile();

    public IReadOnlyList<string> Countries => ["fr"];

    public IReadOnlyList<string> SourceIds => [Cog, Ban, LaPoste];

    public int Order => 10;

    public bool IsCountryAdapter => true;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(FranceMapper).Assembly);

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        if (!inputs.TryGetValue(Cog, out var cog))
        {
            if (inputs.Count > 0)
                build.Warnings.Add("fr: BAN and La Poste need the COG's communes; nothing mapped without fr-cog.");
            return Task.CompletedTask;
        }
        new Run(build, cog, inputs.GetValueOrDefault(Ban), inputs.GetValueOrDefault(LaPoste)).Map();
        return Task.CompletedTask;
    }

    internal static IEnumerable<RefCsvRow> Csv(SourceInput input, Regex pattern, char delimiter, Encoding encoding)
        => input.Files.Where(f => pattern.IsMatch(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal)
            .SelectMany(f => RefCsv.ReadRecords(f.Open(), delimiter, encoding));

    private static DateOnly? Day(string text)
        => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    /// <summary>One mapping pass.</summary>
    private sealed class Run(RefDataBuild build, SourceInput cog, SourceInput? ban, SourceInput? laPoste)
    {
        private string country = string.Empty;
        private readonly Dictionary<string, string> regionOfDepartement = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> currentDepartement = new(StringComparer.Ordinal);

        /// <summary>Per code: its incarnations (start, id), in date order; a reissue adds one.</summary>
        private readonly Dictionary<string, List<(DateOnly? Start, string Id)>> incarnations = new(StringComparer.Ordinal);

        private List<CogMovement> movements = [];

        public void Map()
        {
            Country(build, "fr", "FRA", "250", FranceProfile.Create(), Cog);
            country = CountryPlace(build, "fr", Cog, null, "fr");
            MapRegions();
            var history = Csv(cog, HistoryFile(), ',', Encoding.UTF8)
                .Where(r => r["TYPECOM"] == "COM")
                .Select(r => new CogCommuneRow(r["COM"], r["LIBELLE"], Day(r["DATE_DEBUT"]) ?? CogStart, Day(r["DATE_FIN"])))
                .ToList();
            movements = [.. Csv(cog, MovementFile(), ',', Encoding.UTF8)
                .Select(r => new CogMovement(r["MOD"], Day(r["DATE_EFF"]) ?? CogStart, r["TYPECOM_AV"], r["COM_AV"], r["LIBELLE_AV"], r["TYPECOM_AP"], r["COM_AP"], r["LIBELLE_AP"]))
                .OrderBy(m => m.Date).ThenBy(m => m.Mod, StringComparer.Ordinal).ThenBy(m => m.CodeBefore, StringComparer.Ordinal).ThenBy(m => m.CodeAfter, StringComparer.Ordinal)];
            foreach (var row in Csv(cog, CommuneFile(), ',', Encoding.UTF8).Where(r => r["TYPECOM"] == "COM"))
                currentDepartement[row["COM"]] = row["DEP"];

            MapCommunes(history);
            MapSubCommunes();
            foreach (var id in incarnations.Values.SelectMany(l => l.Select(i => i.Id)).Distinct(StringComparer.OrdinalIgnoreCase))
                build.Places[id].Periods = MergePeriods(build.Places[id].Periods);
            MapLineage();
            if (ban is not null) MapBan(ban);
            if (laPoste is not null) MapLaPoste(laPoste);
        }

        // ---- regions and départements ----

        private void MapRegions()
        {
            foreach (var row in Csv(cog, RegionFile(), ',', Encoding.UTF8))
            {
                var code = row["REG"];
                var place = build.Place(build.Ledger.Mint(Cog, "insee-reg:" + code, RefIds.Place("fr", "insee-reg", code)));
                place.Periods = [new PlacePeriod { Kind = "fr:region", Role = PlaceRole.Region, Parent = country }];
                place.Names = new TranslatedString { Entries = [Name("fr", row["LIBELLE"], TranslatedStringKind.Official, Cog)] };
                Common(place, "insee-reg", code, FranceProfile.ZoneOfRegion(code));
            }
            foreach (var row in Csv(cog, DepartementFile(), ',', Encoding.UTF8))
            {
                var code = row["DEP"];
                regionOfDepartement[code] = row["REG"];
                var place = build.Place(build.Ledger.Mint(Cog, "insee-dep:" + code, RefIds.Place("fr", "insee-dep", code)));
                place.Periods = [new PlacePeriod { Kind = "fr:department", Role = PlaceRole.Subregion, Parent = build.Ledger.Find("insee-reg:" + row["REG"]) }];
                place.Names = new TranslatedString { Entries = [Name("fr", row["LIBELLE"], TranslatedStringKind.Official, Cog)] };
                Common(place, "insee-dep", code, FranceProfile.ZoneOfRegion(row["REG"]));
            }
        }

        private static string DepartementOfCode(string code) => code.StartsWith("97", StringComparison.Ordinal) ? code[..3] : code[..2];

        private string? DepartementPlace(string code, bool current)
        {
            var dep = current && currentDepartement.TryGetValue(code, out var d) ? d : DepartementOfCode(code);
            return regionOfDepartement.ContainsKey(dep) ? build.Ledger.Find("insee-dep:" + dep) : null;
        }

        private string ZoneOf(string code)
        {
            var dep = currentDepartement.GetValueOrDefault(code) ?? DepartementOfCode(code);
            return regionOfDepartement.TryGetValue(dep, out var region) ? FranceProfile.ZoneOfRegion(region) : FranceProfile.Metropolitan;
        }

        // ---- communes ----

        /// <summary>Whether <paramref name="code"/> was reissued on <paramref name="date"/>: the old commune of the code carries on as déléguée (MOD 32).</summary>
        private bool Reissued(string code, DateOnly date)
            => movements.Any(m => m.Mod == "32" && m.Date == date && m.TypeBefore == "COM" && m.CodeBefore == code && m.TypeAfter == "COMD" && m.CodeAfter == code);

        /// <summary>The code an incoming recode (MOD 41/50) on <paramref name="date"/> came from, if any.</summary>
        private string? RecodedFrom(string code, DateOnly date)
            => movements.FirstOrDefault(m => m.Mod is "41" or "50" && m.Date == date && m.TypeBefore == "COM" && m.TypeAfter == "COM" && m.CodeAfter == code && m.CodeBefore != code)?.CodeBefore;

        /// <summary>The place of <paramref name="code"/> on <paramref name="date"/> (the incarnation started on or before it).</summary>
        private string? PlaceOf(string code, DateOnly date)
            => incarnations.TryGetValue(code, out var list)
                ? list.LastOrDefault(i => i.Start is null || i.Start <= date).Id ?? list[0].Id
                : null;

        /// <summary>The place <paramref name="code"/> named just before <paramref name="date"/>.</summary>
        private string? PlaceBefore(string code, DateOnly date)
            => incarnations.TryGetValue(code, out var list)
                ? list.LastOrDefault(i => i.Start is null || i.Start < date).Id ?? list[0].Id
                : null;

        private void MapCommunes(List<CogCommuneRow> history)
        {
            // Old codes first, so a recode can alias the place its old code minted.
            foreach (var group in history.GroupBy(r => r.Code).OrderBy(g => g.Min(r => r.From)).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                var rows = group.OrderBy(r => r.From).ToList();
                var list = incarnations[group.Key] = [];
                RefPlace? place = null;
                foreach (var row in rows)
                {
                    var from = row.From == CogStart ? (DateOnly?)null : row.From;
                    if (place is null || (from is { } start && Reissued(row.Code, start)))
                    {
                        string id;
                        if (place is not null)
                            id = build.Ledger.Mint(Cog, $"insee:{row.Code}@{from:yyyy-MM-dd}", RefIds.Place("fr", "insee", row.Code, from));
                        else if (from is { } d && RecodedFrom(row.Code, d) is { } old && PlaceBefore(old, d) is { } oldId)
                            id = build.Ledger.Alias(Cog, "insee:" + row.Code, oldId);
                        else
                            id = build.Ledger.Mint(Cog, "insee:" + row.Code, RefIds.Place("fr", "insee", row.Code));
                        list.Add((place is null ? null : from, id));
                        place = build.Place(id);
                        if (place.Codes.Count == 0)
                        {
                            place.OfficialLanguages = [new() { Lang = "fr" }];
                            place.TimeZone = ZoneOf(row.Code);
                            place.Source = [Cog];
                        }
                    }
                    place.Periods.Add(new PlacePeriod
                    {
                        From = from, To = row.To, Kind = "fr:commune", Role = PlaceRole.Municipality,
                        Parent = DepartementPlace(row.Code, current: row.To is null) ?? Warn(row),
                    });
                    AddDated(place.Names, row.Name, from, row.To);
                    AddCode(place, row.Code, from, row.To);
                }
            }
        }

        /// <summary>Adjacent periods with one kind and parent are one period (a rename does not cut a period).</summary>
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

        private string? Warn(CogCommuneRow row)
        {
            build.Warnings.Add($"{Cog}: the département of {row.Code} {row.Name} ({row.From:yyyy-MM-dd}) is not in the edition: no parent.");
            return null;
        }

        private static void AddDated(TranslatedString names, string value, DateOnly? from, DateOnly? to)
        {
            var index = names.Entries.FindIndex(e => e.Value == value && e.ValidTo == from && from is not null);
            if (index >= 0)
                names.Entries[index] = names.Entries[index] with { ValidTo = to };
            else
                names.Entries.Add(Name("fr", value, TranslatedStringKind.Official, Cog, from, to));
        }

        private static void AddCode(RefPlace place, string code, DateOnly? from, DateOnly? to)
        {
            var last = place.Codes.LastOrDefault(c => c.Value == code && c.To == from && from is not null);
            if (last is not null)
                last.To = to;
            else
                place.Codes.Add(new PlaceCode { Scheme = "insee", Value = code, From = from, To = to });
        }

        /// <summary>Communes déléguées and associées: the period one level down after a MOD 32/33 (or a MOD 70 conversion), until MOD 34/35.</summary>
        private void MapSubCommunes()
        {
            foreach (var m in movements.Where(m => m.TypeAfter is "COMD" or "COMA" && m.CodeAfter == m.CodeBefore))
            {
                var place = PlaceBefore(m.CodeBefore, m.Date);
                if (place is null || !build.Places.TryGetValue(place, out var document))
                    continue;
                // The commune it joined: the same mouvement's row into a COM (for MOD 70, the parent it already had).
                var into = movements.FirstOrDefault(x => x.Date == m.Date && x.CodeBefore == m.CodeBefore && x.TypeBefore == m.TypeBefore && x.TypeAfter == "COM");
                var parent = into is not null ? PlaceOf(into.CodeAfter, m.Date) : document.Periods.LastOrDefault(p => p.Role == PlaceRole.Submunicipality)?.Parent;
                if (parent is null) continue;
                // It ends at the next mouvement of this sub-commune (MOD 34/35 into a COM, or a MOD 70 conversion).
                var end = movements.FirstOrDefault(x => x.Date > m.Date && x.TypeBefore is "COMD" or "COMA" && x.CodeBefore == m.CodeBefore && x.TypeBefore == m.TypeAfter)?.Date;
                var previous = document.Periods.LastOrDefault();
                if (previous is not null && previous.To is null && previous.From < m.Date)
                    previous.To = m.Date;
                document.Periods.Add(new PlacePeriod
                {
                    From = m.Date, To = end,
                    Kind = m.TypeAfter == "COMD" ? "fr:commune-deleguee" : "fr:commune-associee",
                    Role = PlaceRole.Submunicipality, Parent = parent,
                });
                // The name of a sub-commune carries on with its period.
                var last = document.Names.Entries.LastOrDefault(e => e.Value == m.NameAfter);
                if (last is not null && last.ValidTo == m.Date)
                    document.Names.Entries[document.Names.Entries.IndexOf(last)] = last with { ValidTo = end };
                else if (last is null)
                    document.Names.Entries.Add(Name("fr", m.NameAfter, TranslatedStringKind.Official, Cog, m.Date, end));
                document.Periods = [.. document.Periods.OrderBy(p => DateKeys.From(p.From))];
            }
        }

        // ---- lineage ----

        private void MapLineage()
        {
            var events = new Dictionary<string, RefLineageEvent>(StringComparer.Ordinal);
            foreach (var group in movements.Where(m => m.TypeAfter == "COM").GroupBy(m => (m.Date, m.Mod, m.CodeAfter)))
            {
                var (date, mod, code) = group.Key;
                var receiver = PlaceOf(code, date);
                if (receiver is null) continue;
                LineageKind kind;
                var operations = new List<LineageOperation>();
                switch (mod)
                {
                    case "10":
                        kind = LineageKind.Rename;
                        operations.Add(new() { Place = receiver, Op = LineageOp.Rename });
                        break;
                    case "41" or "50":
                        kind = LineageKind.Recode;
                        operations.Add(new() { Place = receiver, Op = LineageOp.Recode });
                        break;
                    case "20" or "21":
                        kind = mod == "20" ? LineageKind.Split : LineageKind.Restore;
                        operations.Add(new() { Place = receiver, Op = LineageOp.Create });
                        foreach (var m in group.Where(m => m.CodeBefore != code))
                            if (PlaceBefore(m.CodeBefore, date) is { } from)
                                operations.Add(new() { Place = from, Op = LineageOp.Cede, Into = receiver, Share = SuccessionShare.Part });
                        break;
                    case "30" or "31" or "33" or "34" or "35":
                        kind = LineageKind.Absorb;
                        operations.Add(new() { Place = receiver, Op = LineageOp.Continue });
                        foreach (var m in group.Where(m => m.CodeBefore != code || m.TypeBefore != "COM"))
                        {
                            if (PlaceBefore(m.CodeBefore, date) is not { } from || from == receiver) continue;
                            var demoted = mod == "33" && movements.Any(x => x.Date == date && x.CodeBefore == m.CodeBefore && x.TypeAfter == "COMA");
                            var share = mod == "30" && movements.Count(x => x.Date == date && x.Mod == "30" && x.CodeBefore == m.CodeBefore && x.TypeAfter == "COM") > 1
                                ? SuccessionShare.Part : SuccessionShare.Whole;
                            operations.Add(new() { Place = from, Op = demoted ? LineageOp.Demote : LineageOp.End, Into = receiver, Share = share });
                        }
                        break;
                    case "32":
                        kind = LineageKind.Merge;
                        var created = incarnations[code].Any(i => i.Start == date);
                        operations.Add(new() { Place = receiver, Op = created ? LineageOp.Create : LineageOp.Continue });
                        foreach (var m in group)
                        {
                            if (PlaceBefore(m.CodeBefore, date) is not { } from || from == receiver) continue;
                            var demoted = movements.Any(x => x.Date == date && x.CodeBefore == m.CodeBefore && x.TypeAfter == "COMD");
                            operations.Add(new() { Place = from, Op = demoted ? LineageOp.Demote : LineageOp.End, Into = receiver, Share = SuccessionShare.Whole });
                        }
                        break;
                    default:
                        continue;
                }
                var id = RefIds.LineageEvent("fr", date, kind, "insee", code);
                if (!events.TryGetValue(id, out var lineage))
                {
                    events[id] = lineage = new RefLineageEvent { Id = id, Country = "fr", Date = date, Kind = kind };
                    lineage.Sources.Add(new LineageSource { Source = Cog, Date = date, Note = $"INSEE mouvement {mod}" });
                }
                foreach (var op in operations.Where(op => !lineage.Operations.Any(o => o.Place == op.Place && o.Op == op.Op)))
                    lineage.Operations.Add(op);
            }
            foreach (var lineage in events.Values)
                build.AddLineage(lineage);
        }

        // ---- BAN and La Poste ----

        private string? Current(string code) => incarnations.TryGetValue(code, out var list) ? list[^1].Id : null;

        private void MapBan(SourceInput input)
        {
            foreach (var row in Csv(input, BanFile(), ';', Encoding.UTF8))
            {
                var code = row["code_insee"].ToUpperInvariant();
                var municipality = Current(code);
                var postcode = row["code_postal"];
                var name = row["nom_voie"];
                if (municipality is null || name.Length == 0 || postcode.Length != 5)
                    continue;
                var toponym = row["id_ban_toponyme"];
                string streetId;
                if (toponym.Length > 0)
                    streetId = build.Ledger.Mint(Ban, "ban-toponyme:" + toponym, RefIds.Street("fr", "ban", toponym));
                else
                {
                    var key = row["id_fantoir"].Length > 0 ? row["id_fantoir"] : string.Join('_', row["id"].Split('_').Take(2));
                    key = key.ToUpperInvariant();
                    streetId = build.Ledger.Mint(Ban, "ban-voie:" + key, RefIds.Street("fr", "ban-voie", key));
                }
                var street = build.Street(streetId, "fr");
                if (street.Names.Entries.Count == 0)
                {
                    street.Names = new TranslatedString { Entries = [Name("fr", name, TranslatedStringKind.Official, Ban)] };
                    street.Type = StreetType.Streetname;
                    street.Codes = [new PlaceCode { Scheme = toponym.Length > 0 ? "ban-toponyme" : "ban-voie", Value = streetId[(streetId.LastIndexOf('/') + 1)..] }];
                    street.Source = [Ban];
                }
                var former = row["code_insee_ancienne_commune"].ToUpperInvariant();
                // The commune déléguée/associée (or former commune) the address lies in, when it is not the commune itself.
                var locality = former.Length > 0 && incarnations.TryGetValue(former, out var list) && list[0].Id != municipality ? list[0].Id : null;
                var postalId = build.Ledger.Mint(Ban, "postcode:" + postcode, RefIds.PostalCode("fr", postcode));
                var postal = build.PostalCode(postalId, "fr", postcode);
                var label = row["libelle_acheminement"];
                var commune = build.Places[municipality].Names.Entries.LastOrDefault()?.Value ?? label;
                var postalName = SameName(label, commune) ? commune : TitleCase(label);
                Locality(postal, postalName, "fr", Ban, municipality, SameName(label, commune) ? municipality : null, main: SameName(label, commune));
                StreetIn(street, municipality, postalId, locality);
            }
        }

        private void MapLaPoste(SourceInput input)
        {
            foreach (var row in Csv(input, LaPosteFile(), ';', Encoding.Latin1))
            {
                var code = row["#Code_commune_INSEE"].ToUpperInvariant();
                var postcode = row["Code_postal"];
                var municipality = Current(code);
                if (municipality is null || postcode.Length != 5) continue;
                var line5 = row["Ligne_5"];
                var label = row["Libellé_d_acheminement"];
                var commune = build.Places[municipality].Names.Entries.LastOrDefault()?.Value ?? label;
                var postalId = build.Ledger.Mint(LaPoste, "postcode:" + postcode, RefIds.PostalCode("fr", postcode));
                var postal = build.PostalCode(postalId, "fr", postcode);
                if (line5.Length == 0)
                {
                    Locality(postal, SameName(label, commune) ? commune : TitleCase(label), "fr", LaPoste, municipality, SameName(label, commune) ? municipality : null, main: true);
                    continue;
                }
                // Ligne 5: a commune déléguée, associée or lieu-dit served by the code (ARBIGNIEU, ST BOIS, BROU, MEZZAVIA).
                var sub = SubCommunes(municipality).FirstOrDefault(p => p.Names.Entries.Any(e => SameName(line5, e.Value)));
                var name = sub?.Names.Entries.LastOrDefault()?.Value ?? TitleCase(line5);
                Locality(postal, name, "fr", LaPoste, municipality, sub?.Id);
            }
        }

        private Dictionary<string, List<RefPlace>>? subCommunes;

        /// <summary>The current communes déléguées and associées of <paramref name="municipality"/> (indexed once).</summary>
        private List<RefPlace> SubCommunes(string municipality)
        {
            subCommunes ??= build.Places.Values
                .Where(p => p.Country == "fr")
                .SelectMany(p => p.Periods.Where(x => x.Role == PlaceRole.Submunicipality && x.To is null && x.Parent is not null).Select(x => (Parent: x.Parent!, Place: p)))
                .GroupBy(x => x.Parent, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Place).OrderBy(p => p.Id, StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);
            return subCommunes.GetValueOrDefault(municipality) ?? [];
        }

        /// <summary>Postal labels are upper case without accents and abbreviate Saint/Sainte (<c>ST BOIS</c> = Saint-Bois).</summary>
        internal static bool SameName(string label, string name) => PostalKey(label) == PostalKey(name);

        private static string PostalKey(string name)
            => string.Concat(RefSearchKeys.Tokenize(RefSearchKeys.Fold(name)).Select(t => t switch { "st" => "saint", "ste" => "sainte", _ => t }));

        private static readonly HashSet<string> Particles = ["de", "du", "des", "la", "le", "les", "en", "sur", "sous", "et", "aux", "au", "lès", "lez"];

        internal static string TitleCase(string upper)
        {
            var result = new StringBuilder();
            var word = new StringBuilder();
            void Flush()
            {
                var w = word.ToString().ToLowerInvariant();
                w = w switch { "st" => "saint", "ste" => "sainte", _ => w };
                result.Append(result.Length > 0 && Particles.Contains(w) ? w : w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w);
                word.Clear();
            }
            foreach (var c in upper.Trim())
            {
                if (c is ' ' or '-' or '\'')
                {
                    Flush();
                    result.Append(c);
                }
                else
                    word.Append(c);
            }
            Flush();
            return result.ToString();
        }

        private static void Common(RefPlace place, string scheme, string code, string zone)
        {
            place.OfficialLanguages = [new() { Lang = "fr" }];
            place.Codes = [new PlaceCode { Scheme = scheme, Value = code }];
            place.TimeZone = zone;
            place.Source = [Cog];
        }
    }
}
