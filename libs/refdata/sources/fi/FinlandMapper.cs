using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Finland;

/// <summary>
/// The Finnish mapper (PRD §4.4, plan R8). Statistics Finland publishes one version of each list per year and no
/// successor links in the classification service, so:
/// <list type="bullet">
/// <item>a municipality's life is the run of yearly versions that list its code (a code listed again after a gap is a
/// new place with an <c>@date</c> suffix); its names (fi, sv) and its language status are dated per version;</item>
/// <item>its region is the correspondence table of each year, resolved to the region place holding that code;</item>
/// <item>a code that disappears ends its place with an <b>unresolved</b> lineage event (Q-R4): the merger list is not
/// part of the classification service (Honkajoki 099 → Kankaanpää 214, 2021).</item>
/// </list>
/// Streets and postcodes come from Ryhti's address points.
/// </summary>
internal sealed class FinlandMapper : IReferenceDataMapper
{
    public const string Zone = "Europe/Helsinki";

    public IReadOnlyList<string> Countries => ["fi"];
    public IReadOnlyList<string> SourceIds => [FinlandClassificationSource.SourceId, FinlandRyhtiSource.SourceId];
    public int Order => 10;
    public bool IsCountryAdapter => true;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(FinlandMapper).Assembly);

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        var run = new Run(build);
        run.MapCountry();
        if (inputs.TryGetValue(FinlandClassificationSource.SourceId, out var classifications))
            run.MapClassifications(classifications);
        if (inputs.TryGetValue(FinlandRyhtiSource.SourceId, out var ryhti))
            foreach (var file in ryhti.Files.OrderBy(f => f.Name, StringComparer.Ordinal))
                run.MapAddresses(file);
        return Task.CompletedTask;
    }

    /// <summary>One yearly version of a list: code → (fi, sv) names.</summary>
    private sealed class Version
    {
        public Dictionary<string, (string? Fi, string? Sv)> Names { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Region { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> LanguageStatus { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Run(RefDataBuild build)
    {
        private string countryPlace = string.Empty;
        private readonly Dictionary<(string Scheme, string Code), List<(string Place, DateOnly? From, DateOnly? To)>> holders = [];
        private readonly Dictionary<string, List<string>> languagesOf = new(StringComparer.OrdinalIgnoreCase);

        public void MapCountry()
        {
            Country(build, "fi", "FIN", "246", FinlandProfile.Create(), FinlandClassificationSource.SourceId);
            countryPlace = CountryPlace(build, "fi", FinlandClassificationSource.SourceId, Zone);
            var place = build.Place(countryPlace);
            // The embedded CLDR subset has de/en/fr/nl only: the endonyms are hand-authored like the profile.
            if (!place.Names.Entries.Any(e => e.Tag == "fi"))
                place.Names.Entries.InsertRange(0, [Name("fi", "Suomi", TranslatedStringKind.Official, "profile"), Name("sv", "Finland", TranslatedStringKind.Official, "profile")]);
            place.OfficialLanguages = [new() { Lang = "fi" }, new() { Lang = "sv" }];
        }

        public void MapClassifications(SourceInput input)
        {
            var municipalities = new SortedDictionary<DateOnly, Version>();
            var regions = new SortedDictionary<DateOnly, Version>();
            foreach (var file in input.Files.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                if (FinlandClassificationSource.ItemsFile().Match(file.Name) is { Success: true } items)
                {
                    var target = items.Groups["classification"].Value == "kunta" ? municipalities : regions;
                    var version = VersionAt(target, items.Groups["date"].Value);
                    var lang = items.Groups["lang"].Value;
                    using var stream = file.Open();
                    foreach (var item in JsonNode.Parse(stream)!.AsArray())
                    {
                        var code = item!["code"]!.GetValue<string>();
                        var name = item["classificationItemNames"]?.AsArray().Select(n => n?["name"]?.GetValue<string>()).FirstOrDefault(n => n is not null);
                        var (fi, sv) = version.Names.GetValueOrDefault(code);
                        version.Names[code] = lang == "fi" ? (name, sv) : (fi, name);
                    }
                }
                else if (FinlandClassificationSource.MapFile().Match(file.Name) is { Success: true } map)
                {
                    var version = VersionAt(municipalities, map.Groups["date"].Value);
                    using var stream = file.Open();
                    foreach (var row in JsonNode.Parse(stream)!.AsArray())
                    {
                        var from = row!["sourceLocalId"]!.GetValue<string>().Split('/')[^1];
                        var to = row["targetLocalId"]!.GetValue<string>().Split('/')[^1];
                        (map.Groups["target"].Value == "maakunta" ? version.Region : version.LanguageStatus)[from] = to;
                    }
                }
            }

            MapUnits(regions, "maakunta", "fi:region", PlaceRole.Region, (_, _) => countryPlace, (_, _) => null);
            MapUnits(municipalities, "kunta", "fi:municipality", PlaceRole.Municipality,
                (code, version) => version.Region.TryGetValue(code, out var region) ? Holder("maakunta", region, municipalities.First(v => v.Value == version).Key) ?? countryPlace : countryPlace,
                (code, version) => version.LanguageStatus.TryGetValue(code, out var status) ? status : null);
        }

        private static Version VersionAt(SortedDictionary<DateOnly, Version> versions, string date)
        {
            var day = DateOnly.ParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture);
            if (!versions.TryGetValue(day, out var version)) versions[day] = version = new Version();
            return version;
        }

        /// <summary>Language status (Statistics Finland's kielisuhde): 0 Finnish, 1 bilingual Finnish majority, 2 Swedish, 3 bilingual Swedish majority.</summary>
        private static List<string> Languages(string? status) => status switch
        {
            "1" => ["fi", "sv"],
            "2" => ["sv"],
            "3" => ["sv", "fi"],
            _ => ["fi"],
        };

        private void MapUnits(SortedDictionary<DateOnly, Version> versions, string scheme, string kind, PlaceRole role,
            Func<string, Version, string> parentOf, Func<string, Version, string?> statusOf)
        {
            var dates = versions.Keys.ToList();
            if (dates.Count == 0) return;
            var minted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in versions.Values.SelectMany(v => v.Names.Keys).Distinct().OrderBy(c => c, StringComparer.Ordinal))
            {
                string? id = null;
                DateOnly? lifeStart = null;
                for (var i = 0; i < dates.Count; i++)
                {
                    var version = versions[dates[i]];
                    if (!version.Names.TryGetValue(code, out var names))
                    {
                        if (id is not null)
                        {
                            End(id, scheme, code, dates[i]);
                            Hold(scheme, code, id, lifeStart, dates[i]);
                            id = null;
                        }
                        continue;
                    }
                    DateOnly? from = i == 0 ? null : dates[i];
                    DateOnly? to = i + 1 < dates.Count ? dates[i + 1] : null;
                    if (id is null)
                    {
                        lifeStart = from;
                        var plain = RefIds.Place("fi", scheme, code);
                        var identity = from is null ? $"{scheme}:{code}" : $"{scheme}:{code}@{from:yyyy-MM-dd}";
                        // A code listed again after a gap names a new unit: the plain id stays with the first one.
                        var rule = minted.Add(plain) ? plain : RefIds.Place("fi", scheme, code, from);
                        id = build.Ledger.Mint(FinlandClassificationSource.SourceId, identity, rule);
                        var created = build.Place(id);
                        created.TimeZone = Zone;
                        AddSource(created.Source, FinlandClassificationSource.SourceId);
                        created.Codes.Add(new PlaceCode { Scheme = scheme, Value = code, From = from });
                    }
                    var place = build.Place(id);
                    var languages = Languages(statusOf(code, version));
                    languagesOf[id] = languages;
                    Extend(place.Periods, from, to, p => p.Kind == kind && p.Parent == parentOf(code, version),
                        () => new PlacePeriod { From = from, To = to, Kind = kind, Role = role, Parent = parentOf(code, version) });
                    foreach (var (lang, value) in new[] { ("fi", names.Fi), ("sv", names.Sv) })
                        // A Swedish name equal to the Finnish one (Kankaanpää) is no name of its own.
                        if (value is { Length: > 0 } && !(lang == "sv" && value == names.Fi && !languages.Contains("sv")))
                            AddName(place.Names, lang, value, languages.Contains(lang) ? TranslatedStringKind.Official : TranslatedStringKind.Exonym, from, to);
                    foreach (var lang in languages)
                        ExtendLanguage(place.OfficialLanguages, lang, from, to);
                }
                if (id is not null)
                {
                    Hold(scheme, code, id, lifeStart, null);
                }
            }
            // A code's dated value: closed where its place ended.
            foreach (var place in build.Places.Values.Where(p => p.Codes.Any(c => c.Scheme == scheme)))
                foreach (var code in place.Codes.Where(c => c.Scheme == scheme))
                    code.To = place.Periods.Count > 0 && place.Periods.All(p => p.To is not null) ? place.Periods.Max(p => p.To) : null;
        }

        private void Hold(string scheme, string code, string id, DateOnly? from, DateOnly? to)
        {
            if (!holders.TryGetValue((scheme, code), out var list)) holders[(scheme, code)] = list = [];
            list.Add((id, from, to));
        }

        private string? Holder(string scheme, string code, DateOnly day)
            => holders.GetValueOrDefault((scheme, code))?.FirstOrDefault(h => (h.From is null || h.From <= day) && (h.To is null || day < h.To)).Place;

        /// <summary>The unit's code disappears from the list: the place ends; the classification names no successor (Q-R4).</summary>
        private void End(string id, string scheme, string code, DateOnly day)
            => build.AddLineage(new RefLineageEvent
            {
                Id = RefIds.LineageEvent("fi", day, LineageKind.Absorb, scheme, code),
                Country = "fi",
                Date = day,
                Kind = LineageKind.Absorb,
                Unresolved = true,
                Operations = [new LineageOperation { Place = id, Op = LineageOp.End }],
                Sources = [new LineageSource { Source = FinlandClassificationSource.SourceId, Date = day, Note = $"{scheme} {code} is not listed from {day:yyyy-MM-dd}; the classification service publishes no successor" }],
            });

        private static void Extend(List<PlacePeriod> periods, DateOnly? from, DateOnly? to, Func<PlacePeriod, bool> same, Func<PlacePeriod> create)
        {
            if (periods.Count > 0 && periods[^1].To == from && from is not null && same(periods[^1]))
                periods[^1].To = to;
            else
                periods.Add(create());
        }

        private static void ExtendLanguage(List<DatedLanguage> languages, string lang, DateOnly? from, DateOnly? to)
        {
            var last = languages.LastOrDefault(l => l.Lang == lang);
            if (last is not null && last.To == from && from is not null)
                last.To = to;
            else if (last is null || last.To is not null)
                languages.Add(new DatedLanguage { Lang = lang, From = from, To = to });
        }

        private static void AddName(TranslatedString names, string lang, string value, TranslatedStringKind kind, DateOnly? from, DateOnly? to)
        {
            var index = names.Entries.FindLastIndex(e => e.Tag == lang);
            if (index >= 0 && names.Entries[index] is var last && last.Value == value && last.Kind == kind && last.ValidTo == from && from is not null)
            {
                names.Entries[index] = last with { ValidTo = to };
                return;
            }
            names.Entries.Add(Name(lang, value, kind, FinlandClassificationSource.SourceId, from, to));
        }

        /// <summary>Streets and postcodes from one page of Ryhti address points.</summary>
        public void MapAddresses(SourceFile file)
        {
            using var stream = file.Open();
            var root = JsonNode.Parse(stream)!;
            foreach (var feature in root["features"]?.AsArray() ?? [])
            {
                var p = feature!["properties"]!;
                string? Text(string name) => p[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
                var code = Text("municipality_number");
                var fi = Text("address_name_fin");
                var sv = Text("address_name_swe");
                if (code is null || (fi ?? sv) is not { } streetName) continue;
                var municipality = holders.GetValueOrDefault(("kunta", code))?.FirstOrDefault(h => h.To is null).Place;
                if (municipality is null)
                {
                    build.Warnings.Add($"{FinlandRyhtiSource.SourceId}: municipality {code} is not current in the classification.");
                    continue;
                }
                var languages = languagesOf.GetValueOrDefault(municipality) ?? ["fi"];
                var placeCode = municipality[(municipality.LastIndexOf('/') + 1)..].Replace('@', '-');
                // Ryhti has no street identifier: a street is its name within the municipality (folded, so "ä" and "a" meet).
                var key = Key(streetName);
                var streetId = build.Ledger.Mint(FinlandRyhtiSource.SourceId, $"katu:{placeCode}-{key}", RefIds.Street("fi", "k" + placeCode, key));
                var street = build.Street(streetId, "fi");
                if (street.Names.Entries.Count == 0)
                {
                    foreach (var (lang, value) in new[] { ("fi", fi), ("sv", sv) }.OrderBy(n => languages.IndexOf(n.Item1) is var i and >= 0 ? i : 9))
                        if (value is not null)
                            street.Names.Entries.Add(Name(lang, value, languages.Contains(lang) ? TranslatedStringKind.Official : TranslatedStringKind.Exonym, FinlandRyhtiSource.SourceId));
                    street.Codes = [new PlaceCode { Scheme = "ryhti-street", Value = $"{code}-{key}" }];
                    street.Type = StreetType.Streetname;
                }
                AddSource(street.Source, FinlandRyhtiSource.SourceId);

                string? postalId = null;
                if (Text("postal_code") is { Length: 5 } postcode)
                {
                    postalId = build.Ledger.Mint(FinlandRyhtiSource.SourceId, "postinumero:" + postcode, RefIds.PostalCode("fi", postcode));
                    var postal = build.PostalCode(postalId, "fi", postcode);
                    var officeFi = Text("postal_office_fin");
                    var officeSv = Text("postal_office_swe");
                    var mainLang = languages[0];
                    var mainName = mainLang == "sv" ? officeSv ?? officeFi : officeFi ?? officeSv;
                    if (mainName is not null)
                    {
                        var locality = Locality(postal, TitleCase(mainName), mainLang, FinlandRyhtiSource.SourceId, municipality, main: true);
                        var otherLang = mainLang == "fi" ? "sv" : "fi";
                        var other = otherLang == "sv" ? officeSv : officeFi;
                        if (other is not null && other != mainName && !locality.Names.Entries.Any(e => e.Tag == otherLang))
                            locality.Names.Entries.Add(Name(otherLang, TitleCase(other), languages.Contains(otherLang) ? TranslatedStringKind.Official : TranslatedStringKind.Exonym, FinlandRyhtiSource.SourceId));
                    }
                }
                StreetIn(street, municipality, postalId);
            }
        }
    }

    /// <summary>"YLÄ-KOLKKI" → "Ylä-Kolkki": postal offices are written in capitals.</summary>
    internal static string TitleCase(string upper)
        => string.Concat(upper.ToLowerInvariant().Select((c, i) => i == 0 || upper[i - 1] is ' ' or '-' ? char.ToUpperInvariant(c) : c));
}
