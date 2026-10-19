using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Latvia;

/// <summary>
/// The Latvian mapper (PRD §4.4, plan R8): the VZD State Address Register's units, streets and postcodes, with their
/// history from the <c>*_his</c> tables.
/// <list type="bullet">
/// <item><b>Ids</b>: <c>places/lv/var/{KODS}</c> and <c>streets/lv/var/{KODS}</c>; the register keeps deleted codes and
/// never reuses them. The ATVK code (<c>ATRIB</c> of a unit) is a dated code of scheme <c>atvk</c>; in the address file
/// <c>ATRIB</c> is the postcode instead (read per table).</item>
/// <item><b>Periods</b>: each <c>*_his</c> row is an earlier version, valid until its <c>DAT_MOD</c> (inclusive) under
/// its own parent (<c>VKUR_CD</c>); the current row follows it. <c>DAT_BEIG</c> is the last valid day of a deleted
/// (<c>DEL</c>) object. Erroneous rows (<c>ERR</c>) are skipped.</item>
/// <item><b>Lineage</b> without curation: an ended municipality's successors are the municipalities its towns and
/// parishes moved to the next day (Varakļāni → Madona, 2025-07-01: absorb), or a municipality of the same name that
/// starts the next day (the 2021 reform's re-created units: merge). Several successors make a split with part edges; none
/// leaves the event unresolved (Q-R4).</item>
/// <item><b>Catches</b>: the files are UTF-8 with a BOM although their CSVW metadata says ISO-8859-1 (the BOM decides);
/// the district table states 2009-07-01 as the districts' last day while their children's versions end 2009-06-30;
/// a version whose parent did not exist yet (the 2021 units) lies under the parent's same-name predecessor until the
/// parent starts.</item>
/// </list>
/// </summary>
internal sealed partial class LatviaMapper : IReferenceDataMapper
{
    private const string Source = LatviaAddressRegisterSource.SourceId;
    private const string StateCode = "100000000";

    public IReadOnlyList<string> Countries => ["lv"];

    public IReadOnlyList<string> SourceIds => [Source];

    public int Order => 10;

    public bool IsCountryAdapter => true;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(LatviaMapper).Assembly);

    [GeneratedRegex(@"^(?<table>aw_[a-z]+(_his)?)(\.cut)?\.csv$")]
    private static partial Regex TableFile();

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        if (!inputs.TryGetValue(Source, out var input))
            return Task.CompletedTask;
        var tables = new Dictionary<string, List<RefCsvRow>>(StringComparer.Ordinal);
        foreach (var file in input.Files.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (TableFile().Match(file.Name) is not { Success: true } match) continue;
            var table = match.Groups["table"].Value;
            using var stream = file.Open();
            // UTF-8 with a BOM, whatever the CSVW metadata says (ISO-8859-1): the BOM decides.
            var rows = table == "aw_eka"
                ? RefCsv.ReadRecords(stream, ',', Encoding.UTF8).Where(r => r["STATUSS"] == "EKS").ToList()
                : RefCsv.ReadRecords(stream, ',', Encoding.UTF8).Where(r => r["STATUSS"] != "ERR").ToList();
            tables[table] = rows;
        }
        new Run(build, tables).Map();
        return Task.CompletedTask;
    }

    /// <summary>One version of a unit: valid <c>[From, To)</c> under <see cref="Parent"/> with <see cref="Name"/>.</summary>
    private sealed record Version(DateOnly? From, DateOnly? To, string ParentCode, string Name);

    private sealed record Unit(string Code, string Type, string Status, string Atvk, List<Version> Versions)
    {
        public DateOnly? Start => Versions.Count > 0 ? Versions[0].From : null;
        public DateOnly? End => Versions.Count > 0 ? Versions[^1].To : null;
        public string Name => Versions[^1].Name;
    }

    private sealed class Run(RefDataBuild build, Dictionary<string, List<RefCsvRow>> tables)
    {
        private readonly Dictionary<string, Unit> units = new(StringComparer.Ordinal);
        private string countryPlace = string.Empty;

        public void Map()
        {
            MapCountry();
            foreach (var table in new[] { "aw_rajons", "aw_novads", "aw_pilseta", "aw_pagasts", "aw_ciems" })
                ReadUnits(table);
            FixParentsBeforeTheirStart();
            foreach (var unit in units.Values.OrderBy(u => u.Code, StringComparer.Ordinal))
                MapUnit(unit);
            DeriveLineage();
            MapStreets();
        }

        private void MapCountry()
        {
            Country(build, "lv", "LVA", "428", LatviaProfile.Create(), Source);
            countryPlace = CountryPlace(build, "lv", Source, LatviaProfile.Zone, "lv");
        }

        private static DateOnly? Day(string text) => Date(text, "yyyy.MM.dd");

        /// <summary>The last valid day (inclusive) → the exclusive end of a period.</summary>
        private static DateOnly? After(string text) => Day(text)?.AddDays(1);

        private void ReadUnits(string table)
        {
            if (!tables.TryGetValue(table, out var current)) return;
            var history = tables.GetValueOrDefault(table + "_his") ?? [];
            var byCode = history.GroupBy(r => r["KODS"]).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            foreach (var row in current)
            {
                var code = row["KODS"];
                var versions = new List<Version>();
                var start = Day(row["DAT_SAK"]);
                var from = start;
                foreach (var old in (byCode.GetValueOrDefault(code) ?? []).OrderBy(r => Day(r["DAT_MOD"]) ?? DateOnly.MinValue))
                {
                    var to = After(old["DAT_MOD"]);
                    if (to is null || (from is not null && to <= from)) continue;
                    versions.Add(new Version(from, to, old["VKUR_CD"], old["NOSAUKUMS"]));
                    from = to;
                }
                var end = row["STATUSS"] == "DEL" ? After(row["DAT_BEIG"]) : null;
                if (end is null || from is null || end > from)
                    versions.Add(new Version(from, end, row["VKUR_CD"], row["NOSAUKUMS"]));
                units[code] = new Unit(code, row["TIPS_CD"], row["STATUSS"], table == "aw_rajons" ? string.Empty : row["ATRIB"], versions);
            }
        }

        /// <summary>
        /// A version under a parent that did not exist yet (the register re-pointed a town to the 2021 municipality
        /// without a history row): until the parent starts, the town lay under the parent's same-name predecessor.
        /// </summary>
        private void FixParentsBeforeTheirStart()
        {
            foreach (var unit in units.Values)
            {
                for (var i = 0; i < unit.Versions.Count; i++)
                {
                    var v = unit.Versions[i];
                    if (!units.TryGetValue(v.ParentCode, out var parent) || parent.Start is not { } parentStart) continue;
                    if (v.From is not null && v.From >= parentStart) continue;
                    if (v.To is not null && v.To <= parentStart) continue;
                    var predecessor = units.Values.FirstOrDefault(p => p.Type == parent.Type && p.Code != parent.Code
                        && p.End == parentStart && p.Name == parent.Name);
                    if (predecessor is null)
                    {
                        build.Warnings.Add($"lv-var: {unit.Code} lies under {parent.Code} before {parent.Code} starts ({parentStart:yyyy-MM-dd}) and no predecessor of that name exists.");
                        continue;
                    }
                    unit.Versions[i] = v with { To = parentStart };
                    unit.Versions.Insert(i + 1, v with { From = parentStart });
                    unit.Versions[i] = unit.Versions[i] with { ParentCode = predecessor.Code };
                    i++;
                }
            }
        }

        private string UnitId(string code)
            => code == StateCode ? countryPlace : build.Ledger.Mint(Source, "var:" + code, RefIds.Place("lv", "var", code));

        private (string Kind, PlaceRole Role) KindOf(Unit unit, Version version) => unit.Type switch
        {
            "102" => ("lv:district", PlaceRole.Subregion),
            "113" => ("lv:municipality", PlaceRole.Municipality),
            // A town directly under the state is a state city (valstspilsēta): a municipality of its own.
            "104" when version.ParentCode == StateCode => ("lv:municipality", PlaceRole.Municipality),
            "104" => ("lv:town", PlaceRole.Submunicipality),
            "105" => ("lv:parish", PlaceRole.Submunicipality),
            _ => ("lv:village", PlaceRole.Locality),
        };

        private void MapUnit(Unit unit)
        {
            var place = build.Place(UnitId(unit.Code));
            place.Periods = [];
            foreach (var version in unit.Versions)
            {
                var (kind, role) = KindOf(unit, version);
                var parent = version.ParentCode.Length == 0 ? countryPlace : UnitId(version.ParentCode);
                var last = place.Periods.LastOrDefault();
                if (last is not null && last.Kind == kind && last.Parent == parent && last.To == version.From)
                    last.To = version.To;
                else
                    place.Periods.Add(new PlacePeriod { From = version.From, To = version.To, Kind = kind, Role = role, Parent = parent });
            }

            place.Names = new TranslatedString();
            var names = unit.Versions.GroupBy(v => v.Name).Select(g => (Name: g.Key, From: g.First().From, To: g.Last().To)).ToList();
            foreach (var (name, from, to) in names)
            {
                var isLast = name == unit.Name;
                place.Names.Entries.Add(Name("lv", name, isLast ? TranslatedStringKind.Official : TranslatedStringKind.Historic, Source,
                    names.Count > 1 ? from : null, isLast ? (unit.Status == "DEL" ? unit.End : null) : to));
            }
            place.OfficialLanguages = [new DatedLanguage { Lang = "lv" }];
            place.Codes = [new PlaceCode { Scheme = "var", Value = unit.Code, From = unit.Start, To = unit.End }];
            if (unit.Atvk.Length > 0)
                place.Codes.Add(new PlaceCode { Scheme = "atvk", Value = unit.Atvk, From = unit.Start, To = unit.End });
            place.TimeZone = LatviaProfile.Zone;
            place.Source = [Source];
        }

        // ---- lineage ----

        private void DeriveLineage()
        {
            foreach (var ended in units.Values.Where(u => u.Type == "113" && u.Status == "DEL" && u.End is not null).OrderBy(u => u.Code, StringComparer.Ordinal))
            {
                var day = ended.End!.Value;
                // The municipalities its children lay under from the next day.
                var successors = units.Values
                    .SelectMany(child => child.Versions.Zip(child.Versions.Skip(1)))
                    .Where(pair => pair.First.ParentCode == ended.Code && pair.First.To == day && pair.Second.ParentCode != ended.Code)
                    .Select(pair => pair.Second.ParentCode)
                    .ToHashSet(StringComparer.Ordinal);
                // A municipality of the same name starting that day (the 2021 reform re-created units under new codes).
                foreach (var same in units.Values.Where(u => u.Type == "113" && u.Start == day && u.Name == ended.Name && u.Code != ended.Code))
                    successors.Add(same.Code);
                successors.RemoveWhere(s => !units.ContainsKey(s));

                if (successors.Count == 0)
                {
                    build.AddLineage(new RefLineageEvent
                    {
                        Id = RefIds.LineageEvent("lv", day, LineageKind.Merge, "var", ended.Code),
                        Country = "lv", Date = day, Kind = LineageKind.Merge, Unresolved = true,
                        Operations = [new LineageOperation { Place = UnitId(ended.Code), Op = LineageOp.End }],
                        Sources = [new LineageSource { Source = Source, Date = day, Note = $"{ended.Code} ends {day.AddDays(-1):yyyy-MM-dd}; no successor derivable from the register" }],
                    });
                    continue;
                }

                var share = successors.Count == 1 ? SuccessionShare.Whole : SuccessionShare.Part;
                foreach (var successor in successors.OrderBy(s => s, StringComparer.Ordinal))
                {
                    var created = units[successor].Start == day;
                    var kind = successors.Count > 1 ? LineageKind.Split : created ? LineageKind.Merge : LineageKind.Absorb;
                    var id = RefIds.LineageEvent("lv", day, kind, "var", kind == LineageKind.Split ? ended.Code : successor);
                    if (!build.Lineage.TryGetValue(id, out var lineage))
                    {
                        lineage = new RefLineageEvent
                        {
                            Id = id, Country = "lv", Date = day, Kind = kind,
                            Sources = [],
                        };
                        if (kind != LineageKind.Split)
                            lineage.Operations.Add(new LineageOperation { Place = UnitId(successor), Op = created ? LineageOp.Create : LineageOp.Receive });
                        build.AddLineage(lineage);
                    }
                    lineage.Operations.Add(new LineageOperation { Place = UnitId(ended.Code), Op = share == SuccessionShare.Whole ? LineageOp.End : LineageOp.Cede, Into = UnitId(successor), Share = share });
                    lineage.Sources.Add(new LineageSource { Source = Source, Date = day, Note = $"{ended.Code} {ended.Name} ends {day.AddDays(-1):yyyy-MM-dd}; its units lie under {successor} from {day:yyyy-MM-dd}" });
                }
            }
        }

        // ---- streets and postcodes ----

        /// <summary>The municipality a settlement lay in during each of its periods (a state city is its own).</summary>
        private IEnumerable<(DateOnly? From, DateOnly? To, string Municipality)> MunicipalitiesOf(string settlementCode)
        {
            if (!units.TryGetValue(settlementCode, out var unit)) yield break;
            foreach (var version in unit.Versions)
            {
                if (unit.Type == "113" || (unit.Type == "104" && version.ParentCode == StateCode))
                    yield return (version.From, version.To, UnitId(unit.Code));
                else if (units.TryGetValue(version.ParentCode, out var parent) && parent.Type == "105")
                {
                    // A village in a parish: the parish's municipality at the time (the parish's own versions decide).
                    foreach (var p in MunicipalitiesOf(parent.Code))
                        if ((p.To is null || version.From is null || p.To > version.From) && (version.To is null || p.From is null || p.From < version.To))
                            yield return (Max(p.From, version.From), Min(p.To, version.To), p.Municipality);
                }
                else if (version.ParentCode.Length > 0 && units.ContainsKey(version.ParentCode) && units[version.ParentCode].Type is "113" or "104")
                    yield return (version.From, version.To, UnitId(version.ParentCode));
            }
        }

        private static DateOnly? Max(DateOnly? a, DateOnly? b) => a is null ? b : b is null ? a : a > b ? a : b;
        private static DateOnly? Min(DateOnly? a, DateOnly? b) => a is null ? b : b is null ? a : a < b ? a : b;

        private void MapStreets()
        {
            if (!tables.TryGetValue("aw_iela", out var streets)) return;
            var history = (tables.GetValueOrDefault("aw_iela_his") ?? []).GroupBy(r => r["KODS"]).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            // Postcodes per street (and per settlement for addresses without a street), from the address file.
            var postcodes = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (var address in tables.GetValueOrDefault("aw_eka") ?? [])
            {
                var postcode = address["ATRIB"];
                if (postcode.Length == 0) continue;
                if (!postcodes.TryGetValue(address["VKUR_CD"], out var set)) postcodes[address["VKUR_CD"]] = set = new(StringComparer.Ordinal);
                set.Add(postcode);
            }

            foreach (var row in streets.OrderBy(r => r["KODS"], StringComparer.Ordinal))
            {
                var code = row["KODS"];
                var settlement = row["VKUR_CD"];
                if (!units.TryGetValue(settlement, out var settlementUnit))
                {
                    build.Warnings.Add($"lv-var: street {code} lies in {settlement}, which the cut does not hold.");
                    continue;
                }
                var street = build.Street(build.Ledger.Mint(Source, "var-street:" + code, RefIds.Street("lv", "var", code)), "lv");
                var start = Day(row["DAT_SAK"]);
                var end = row["STATUSS"] == "DEL" ? After(row["DAT_BEIG"]) : null;

                street.Names = new TranslatedString();
                var from = start;
                foreach (var old in (history.GetValueOrDefault(code) ?? []).OrderBy(r => Day(r["DAT_MOD"]) ?? DateOnly.MinValue))
                {
                    var to = After(old["DAT_MOD"]);
                    if (old["NOSAUKUMS"] != row["NOSAUKUMS"] && !street.Names.Entries.Any(e => e.Value == old["NOSAUKUMS"]))
                        street.Names.Entries.Add(Name("lv", old["NOSAUKUMS"], TranslatedStringKind.Historic, Source, from, to));
                    from = to ?? from;
                }
                street.Names.Entries.Insert(0, Name("lv", row["NOSAUKUMS"], TranslatedStringKind.Official, Source));
                street.Type = StreetType.Streetname;
                street.Codes = [new PlaceCode { Scheme = "var", Value = code, From = start, To = end }];
                street.Source = [Source];

                var settlementId = UnitId(settlementUnit.Code);
                var locality = settlementUnit.Type == "104" && settlementUnit.Versions[^1].ParentCode == StateCode ? null : settlementId;
                var codes = postcodes.GetValueOrDefault(code) ?? [];
                foreach (var (mFrom, mTo, municipality) in MunicipalitiesOf(settlementUnit.Code))
                {
                    var pFrom = Max(mFrom, start);
                    var pTo = Min(mTo, end);
                    if (pFrom is not null && pTo is not null && pTo <= pFrom) continue;
                    // Postcodes are the register's current ones: attached to the current period only.
                    StreetIn(street, municipality, null, locality, pFrom, pTo);
                    if (pTo is null)
                        foreach (var postcode in codes)
                            StreetIn(street, municipality, PostalCode(postcode, settlementUnit, municipality), locality, pFrom, pTo);
                }
            }
        }

        /// <summary>The postal code document and its locality: the settlement the address lies in (the register's STD form).</summary>
        private string PostalCode(string code, Unit settlement, string municipality)
        {
            var id = build.Ledger.Mint(Source, "postcode:" + code, RefIds.PostalCode("lv", code));
            var postal = build.PostalCode(id, "lv", code);
            Locality(postal, settlement.Name, "lv", Source, municipality, UnitId(settlement.Code), main: true);
            return id;
        }
    }
}
