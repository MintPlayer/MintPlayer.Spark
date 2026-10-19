using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;

namespace MintPlayer.Spark.ReferenceData.Sources.UnitedStates;

/// <summary>
/// The U.S. mapper (PRD §4.4): states and counties by GEOID (<c>places/us/geoid/48301</c>), county changes since 1970
/// as lineage, and streets with their (non-authoritative) ZIP Codes from the address-range files
/// (<c>streets/us/linearid/110764077779</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A GEOID is recodable (Wade Hampton 02-270 → Kusilvak 02-158, 2015): a recode keeps the id the old code minted
/// and adds the new code as a dated code (§4.3).</item>
/// <item>A GEOID can be reissued to a different unit (Petersburg Census Area 02-195 → Petersburg Borough 02-195, 2013):
/// the new unit gets an <c>@yyyy-MM-dd</c> id suffix.</item>
/// <item>Changes are read only for states present in the code lists; entries of other states are skipped.</item>
/// </list>
/// </remarks>
internal sealed partial class UnitedStatesMapper : IReferenceDataMapper
{
    private const string Codes = UnitedStatesCodesSource.SourceId;
    private const string Changes = UnitedStatesCountyChangesSource.SourceId;
    private const string Ranges = UnitedStatesAddressRangesSource.SourceId;

    public IReadOnlyList<string> Countries => ["us"];

    public IReadOnlyList<string> SourceIds => [Codes, Changes, Ranges];

    public int Order => 10;

    public bool IsCountryAdapter => true;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(UnitedStatesMapper).Assembly);

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        RefAdapterKit.Country(build, "us", "USA", "840", UnitedStatesProfile.Create(), Codes);
        var run = new Run(build, RefAdapterKit.CountryPlace(build, "us", Codes, null, "en"));
        if (inputs.TryGetValue(Codes, out var codes))
            run.MapStates(codes);
        if (inputs.TryGetValue(Changes, out var changes))
            run.MapChanges(changes);
        if (inputs.TryGetValue(Codes, out codes))
            run.MapCounties(codes);
        if (inputs.TryGetValue(Ranges, out var ranges))
            run.MapStreets(ranges);
        return Task.CompletedTask;
    }

    [GeneratedRegex(@"(?<name>[A-Z][\w'.\- ]*?(?: \(independent\) city)?), (?<state>[A-Z][a-z]+(?: [A-Z][a-z]+)*) \((?<ss>\d{2})-(?<ccc>\d{3})\):? (?<text>.+?) (?:effective|reported as of) (?<date>[A-Z][a-z]+ \d{1,2}, \d{4})")]
    private static partial Regex Entry();

    /// <summary>A unit named in an entry's text: capitalized words (with "of", "and", "the") before "(SS-CCC)".</summary>
    [GeneratedRegex(@"(?<name>[A-Z][\w'.\-]*(?: (?:[A-Z][\w'.\-]*|of|and|the))*) \((?<ss>\d{2})-(?<ccc>\d{3})\)")]
    private static partial Regex Reference();

    [GeneratedRegex(@"^.*(?:Entities|Changes) ")]
    private static partial Regex Heading();

    private enum Section { New, Deleted, NameCode, Boundary }

    private sealed class Run(RefDataBuild build, string country)
    {
        private readonly Dictionary<string, string> states = new(StringComparer.Ordinal);

        /// <summary>The id the current holder of a GEOID has (a recode's old id, a reissue's suffixed id).</summary>
        private readonly Dictionary<string, string> current = new(StringComparer.Ordinal);

        private readonly Dictionary<string, string> countyNames = new(StringComparer.Ordinal);

        public void MapStates(SourceInput input)
        {
            foreach (var file in input.Files.Where(f => UnitedStatesCodesSource.StateFile().IsMatch(f.Name)))
                foreach (var row in RefCsv.ReadRecords(file.Open(), '|', quotes: false))
                {
                    var fips = row["STATEFP"];
                    var id = build.Ledger.Mint(Codes, "geoid:" + fips, RefIds.Place("us", "geoid", fips));
                    var place = build.Place(id);
                    place.Periods = [new PlacePeriod { Kind = "us:state", Role = PlaceRole.Region, Parent = country }];
                    place.Names = new TranslatedString { Entries = [RefAdapterKit.Name("en", row["STATE_NAME"], TranslatedStringKind.Official, Codes)] };
                    place.OfficialLanguages = [new DatedLanguage { Lang = "en" }];
                    place.Codes = [new PlaceCode { Scheme = "geoid", Value = fips }, new PlaceCode { Scheme = "usps", Value = row["STATE"] }, new PlaceCode { Scheme = "gnis", Value = row["STATENS"] }];
                    (place.TimeZone, place.TimeZoneNote) = Zone(fips);
                    place.Source = [Codes];
                    states[fips] = id;
                }
        }

        private (string? Zone, string? Note) Zone(string stateFips)
        {
            if (!UnitedStatesProfile.SingleZoneStates.TryGetValue(stateFips, out var zone))
                return (null, UnitedStatesProfile.SplitStateNote);
            if (build.KnownTimeZones.Count > 0 && !build.KnownTimeZones.Contains(zone))
                return (null, $"{zone} is not in the tz database's zone list.");
            return (zone, null);
        }

        // ---- county changes since 1970 ----

        public void MapChanges(SourceInput input)
        {
            foreach (var file in input.Files.Where(f => UnitedStatesCountyChangesSource.ChangesFile().IsMatch(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                string text;
                using (var reader = new StreamReader(file.Open(), Encoding.UTF8))
                    text = reader.ReadToEnd();
                var headings = new (int At, Section Section)[]
                {
                    (text.IndexOf("New Counties or County Equivalent Entities", StringComparison.Ordinal), Section.New),
                    (text.IndexOf("Deleted Counties or County Equivalent Entities", StringComparison.Ordinal), Section.Deleted),
                    (text.IndexOf("Name and/or Code Changes", StringComparison.Ordinal), Section.NameCode),
                    (text.IndexOf("Substantial County or County Equivalent Boundary Changes", StringComparison.Ordinal), Section.Boundary),
                }.Where(h => h.At >= 0).OrderBy(h => h.At).ToList();

                foreach (Match match in Entry().Matches(text))
                {
                    var ss = match.Groups["ss"].Value;
                    if (!states.ContainsKey(ss)) continue; // a state this import does not carry
                    var section = headings.LastOrDefault(h => h.At <= match.Index).Section;
                    var date = DateOnly.ParseExact(match.Groups["date"].Value, new[] { "MMMM d, yyyy", "MMMM dd, yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None);
                    var name = Heading().Replace(match.Groups["name"].Value, string.Empty).Trim();
                    var code = ss + match.Groups["ccc"].Value;
                    var body = match.Groups["text"].Value;
                    var others = Reference().Matches(body).Select(m => (Name: Heading().Replace(m.Groups["name"].Value, string.Empty).Replace("former ", string.Empty).Trim(), Code: m.Groups["ss"].Value + m.Groups["ccc"].Value)).ToList();
                    var note = $"{name} ({ss}-{match.Groups["ccc"].Value}): {body} ({match.Groups["date"].Value})";
                    switch (section)
                    {
                        case Section.New: New(code, name, date, others, note); break;
                        case Section.Deleted: Deleted(code, name, date, others, body, note); break;
                        case Section.NameCode: NameOrCode(code, name, date, others, body, note); break;
                        case Section.Boundary: Boundary(code, name, date, others, body, note); break;
                    }
                }
            }
        }

        private void New(string code, string name, DateOnly date, List<(string Name, string Code)> predecessors, string note)
        {
            // A GEOID reissued to a different unit: the new unit's id carries the date (§4.3).
            var reissue = predecessors.Any(p => p.Code == code);
            string id;
            if (reissue)
            {
                var oldName = predecessors.First(p => p.Code == code).Name;
                var old = County(code, oldName);
                InitCounty(build.Place(old), code, oldName, from: null);
                End(old, date);
                id = build.Ledger.Mint(Changes, $"geoid:{code}@{date:yyyy-MM-dd}", RefIds.Place("us", "geoid", code, date));
            }
            else
                id = County(code, name);
            current[code] = id;
            var place = build.Place(id);
            InitCounty(place, code, name, from: date);

            var lineage = new RefLineageEvent
            {
                Id = RefIds.LineageEvent("us", date, predecessors.Count > 1 ? LineageKind.Merge : LineageKind.Split, "geoid", code),
                Country = "us", Date = date, Kind = predecessors.Count > 1 ? LineageKind.Merge : LineageKind.Split,
                Operations = [new LineageOperation { Place = id, Op = LineageOp.Create }],
                Sources = [new LineageSource { Source = Changes, Date = date, Note = note }],
            };
            foreach (var (otherName, otherCode) in predecessors.Where(p => p.Code != code || reissue))
            {
                var other = otherCode == code ? build.Ledger.Find("geoid:" + code)! : County(otherCode, otherName);
                lineage.Operations.Add(new LineageOperation { Place = other, Op = otherCode == code ? LineageOp.End : LineageOp.Cede, Into = id, Share = SuccessionShare.Part });
            }
            Add(lineage);
        }

        private void Deleted(string code, string name, DateOnly date, List<(string Name, string Code)> successors, string body, string note)
        {
            var id = County(code, name);
            InitCounty(build.Place(id), code, name, from: null);
            End(id, date);
            var split = body.Contains("Split", StringComparison.OrdinalIgnoreCase) || successors.Count > 1;
            var kind = split ? LineageKind.Split : LineageKind.Absorb;
            var lineage = new RefLineageEvent
            {
                Id = RefIds.LineageEvent("us", date, kind, "geoid", code),
                Country = "us", Date = date, Kind = kind,
                Sources = [new LineageSource { Source = Changes, Date = date, Note = note }],
                Unresolved = successors.Count == 0,
            };
            if (successors.Count == 0)
                lineage.Operations.Add(new LineageOperation { Place = id, Op = LineageOp.End });
            foreach (var (otherName, otherCode) in successors)
            {
                var target = current.GetValueOrDefault(otherCode) ?? County(otherCode, otherName);
                lineage.Operations.Add(new LineageOperation { Place = id, Op = LineageOp.End, Into = target, Share = split ? SuccessionShare.Part : SuccessionShare.Whole });
                if (!split) lineage.Operations.Add(new LineageOperation { Place = target, Op = LineageOp.Receive });
            }
            if (successors.Count == 0) build.Place(id).LineageUnresolved = true;
            Add(lineage);
        }

        private void NameOrCode(string code, string name, DateOnly date, List<(string Name, string Code)> others, string body, string note)
        {
            if (body.StartsWith("Changed name and code to", StringComparison.Ordinal))
                return; // the mirror of the "from" entry, which carries the event
            if (body.StartsWith("Changed name and code from", StringComparison.Ordinal) && others.FirstOrDefault() is { Code.Length: 5 } previous)
            {
                // A recode: the place keeps the id the old code minted; the new code is an alias with its own dates.
                var id = County(previous.Code, previous.Name);
                build.Ledger.Alias(Changes, "geoid:" + code, id);
                current[code] = id;
                var place = build.Place(id);
                InitCounty(place, previous.Code, previous.Name, from: null);
                foreach (var c in place.Codes.Where(c => c.Scheme == "geoid" && c.Value == previous.Code)) c.To = date;
                place.Codes.Add(new PlaceCode { Scheme = "geoid", Value = code, From = date });
                Rename(place, previous.Name, name, date);
                Add(new RefLineageEvent
                {
                    Id = RefIds.LineageEvent("us", date, LineageKind.Recode, "geoid", code),
                    Country = "us", Date = date, Kind = LineageKind.Recode,
                    Operations = [new LineageOperation { Place = id, Op = LineageOp.Recode }, new LineageOperation { Place = id, Op = LineageOp.Rename }],
                    Sources = [new LineageSource { Source = Changes, Date = date, Note = note }],
                });
                return;
            }
            if (body.StartsWith("Name corrected from", StringComparison.Ordinal) || body.StartsWith("Changed name from", StringComparison.Ordinal))
            {
                var id = current.GetValueOrDefault(code) ?? County(code, name);
                var place = build.Place(id);
                InitCounty(place, code, name, from: null);
                var old = Regex.Match(body, @"from (?<old>.+?)(?: \(|$)").Groups["old"].Value.Trim();
                if (old.Length > 0) Rename(place, old, name, date);
                Add(new RefLineageEvent
                {
                    Id = RefIds.LineageEvent("us", date, LineageKind.Rename, "geoid", code),
                    Country = "us", Date = date, Kind = LineageKind.Rename,
                    Operations = [new LineageOperation { Place = id, Op = LineageOp.Rename }],
                    Sources = [new LineageSource { Source = Changes, Date = date, Note = note }],
                });
            }
        }

        private void Boundary(string code, string name, DateOnly date, List<(string Name, string Code)> others, string body, string note)
        {
            // "Added the former independent city of Bedford": the Deleted entry already carries that absorption.
            if (body.StartsWith("Added the former", StringComparison.Ordinal) || others.Count == 0) return;
            var self = current.GetValueOrDefault(code) ?? County(code, name);
            InitCounty(build.Place(self), code, name, from: null);
            var lineage = new RefLineageEvent
            {
                Id = RefIds.LineageEvent("us", date, LineageKind.Cede, "geoid", code),
                Country = "us", Date = date, Kind = LineageKind.Cede,
                Sources = [new LineageSource { Source = Changes, Date = date, Note = note }],
            };
            var taken = body.StartsWith("Part taken", StringComparison.Ordinal);
            foreach (var (otherName, otherCode) in others.Where(o => o.Code != code))
            {
                // A unit created that day under a reused code is the new holder (Petersburg Borough), not the old one.
                var other = taken ? current.GetValueOrDefault(otherCode) ?? County(otherCode, otherName)
                                  : build.Ledger.Find("geoid:" + otherCode) ?? County(otherCode, otherName);
                if (taken)
                    lineage.Operations.Add(new LineageOperation { Place = self, Op = LineageOp.Cede, Into = other, Share = SuccessionShare.Part });
                else
                {
                    lineage.Operations.Add(new LineageOperation { Place = other, Op = LineageOp.Cede, Into = self, Share = SuccessionShare.Part });
                    lineage.Operations.Add(new LineageOperation { Place = self, Op = LineageOp.Receive });
                }
            }
            if (lineage.Operations.Count > 0) Add(lineage);
        }

        private void Add(RefLineageEvent lineage)
        {
            // Two entries of one unit on one day (a page lists a change under two headings) extend one event.
            if (build.Lineage.TryGetValue(lineage.Id!, out var existing))
            {
                existing.Operations.AddRange(lineage.Operations.Where(o => !existing.Operations.Any(e => e.Place == o.Place && e.Op == o.Op && e.Into == o.Into)));
                existing.Sources.AddRange(lineage.Sources);
                return;
            }
            build.AddLineage(lineage);
        }

        private string County(string code, string name)
        {
            countyNames.TryAdd(code, name);
            return build.Ledger.Find("geoid:" + code) ?? build.Ledger.Mint(Changes, "geoid:" + code, RefIds.Place("us", "geoid", code));
        }

        private void InitCounty(RefPlace place, string code, string name, DateOnly? from)
        {
            var state = code[..2];
            if (place.Periods.Count == 0)
            {
                place.Periods = [new PlacePeriod { From = from, Kind = "us:county", Role = PlaceRole.Municipality, Parent = states.GetValueOrDefault(state) }];
                place.Names = new TranslatedString { Entries = [RefAdapterKit.Name("en", name, TranslatedStringKind.Official, Changes, from)] };
                place.OfficialLanguages = [new DatedLanguage { Lang = "en" }];
                place.Codes = [new PlaceCode { Scheme = "geoid", Value = code, From = from }];
                (place.TimeZone, place.TimeZoneNote) = Zone(state);
            }
            else if (from is not null && place.Periods[0].From is null)
                place.Periods[0].From = from;
            RefAdapterKit.AddSource(place.Source, Changes);
        }

        /// <summary>Closes the place's last period, and its open codes and names, on <paramref name="date"/>.</summary>
        private void End(string id, DateOnly date)
        {
            var place = build.Place(id);
            if (place.Periods.Count > 0 && place.Periods[^1].To is null)
                place.Periods[^1].To = date;
            foreach (var code in place.Codes.Where(c => c.To is null)) code.To = date;
            place.Names.Entries = [.. place.Names.Entries.Select(e => e.ValidTo is null ? e with { ValidTo = date } : e)];
        }

        private void Rename(RefPlace place, string old, string name, DateOnly date)
        {
            place.Names.Entries = [.. place.Names.Entries.Select(e => e.Value == old && e.ValidTo is null ? e with { ValidTo = date, Kind = TranslatedStringKind.Historic } : e)];
            if (!place.Names.Entries.Any(e => e.Value == old))
                place.Names.Entries.Add(RefAdapterKit.Name("en", old, TranslatedStringKind.Historic, Changes, to: date));
            if (!place.Names.Entries.Any(e => e.Value == name && e.ValidTo is null))
                place.Names.Entries.Add(RefAdapterKit.Name("en", name, TranslatedStringKind.Official, Changes, from: date));
        }

        // ---- current counties ----

        public void MapCounties(SourceInput input)
        {
            foreach (var file in input.Files.Where(f => UnitedStatesCodesSource.CountyFile().IsMatch(f.Name)))
                foreach (var row in RefCsv.ReadRecords(file.Open(), '|', quotes: false))
                {
                    var code = row["STATEFP"] + row["COUNTYFP"];
                    if (!states.ContainsKey(row["STATEFP"])) continue;
                    var id = current.GetValueOrDefault(code) ?? build.Ledger.Find("geoid:" + code) ?? build.Ledger.Mint(Codes, "geoid:" + code, RefIds.Place("us", "geoid", code));
                    var place = build.Place(id);
                    InitCounty(place, code, row["COUNTYNAME"], from: null);
                    // The code list states the current name: a recode's or correction's newest name, or the only one.
                    if (!place.Names.Entries.Any(e => e.Value == row["COUNTYNAME"] && e.ValidTo is null))
                        place.Names.Entries.Add(RefAdapterKit.Name("en", row["COUNTYNAME"], TranslatedStringKind.Official, Codes));
                    if (!place.Codes.Any(c => c.Scheme == "gnis"))
                        place.Codes.Add(new PlaceCode { Scheme = "gnis", Value = row["COUNTYNS"] });
                    RefAdapterKit.AddSource(place.Source, Codes);
                }
        }

        // ---- address ranges: streets and ZIP Codes ----

        public void MapStreets(SourceInput input)
        {
            foreach (var file in input.Files.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                if (UnitedStatesAddressRangesSource.DbfFile().Match(file.Name) is not { Success: true } match) continue;
                var geoid = match.Groups["geoid"].Value;
                var county = current.GetValueOrDefault(geoid) ?? build.Ledger.Find("geoid:" + geoid);
                if (county is null)
                {
                    build.Warnings.Add($"{Ranges}: {file.Name} names county {geoid}, which the code lists lack: skipped.");
                    continue;
                }
                using var stream = file.Open();
                foreach (var row in DbfReader.Read(stream))
                {
                    var linear = row.GetValueOrDefault("LINEARID") ?? string.Empty;
                    var name = row.GetValueOrDefault("FULLNAME") ?? string.Empty;
                    if (linear.Length == 0 || name.Length == 0) continue;
                    var streetId = build.Ledger.Mint(Ranges, "linearid:" + linear, RefIds.Street("us", "linearid", linear));
                    var street = build.Street(streetId, "us");
                    if (street.Names.Entries.Count == 0)
                    {
                        street.Names = new TranslatedString { Entries = [RefAdapterKit.Name("en", name, TranslatedStringKind.Official, Ranges)] };
                        street.Codes = [new PlaceCode { Scheme = "linearid", Value = linear }];
                        street.Type = StreetType.Streetname;
                        street.Source = [Ranges];
                    }
                    var period = RefAdapterKit.StreetIn(street, county, null);
                    foreach (var zip in new[] { row.GetValueOrDefault("ZIPL"), row.GetValueOrDefault("ZIPR") }.Where(z => z is { Length: 5 }))
                    {
                        var postalId = build.Ledger.Mint(Ranges, "zip:" + zip, RefIds.PostalCode("us", zip!));
                        var postal = build.PostalCode(postalId, "us", zip!);
                        RefAdapterKit.AddSource(postal.Source, Ranges);
                        if (!period.PostalCodes.Contains(postalId, StringComparer.OrdinalIgnoreCase))
                            period.PostalCodes.Add(postalId);
                    }
                }
            }
        }
    }
}
