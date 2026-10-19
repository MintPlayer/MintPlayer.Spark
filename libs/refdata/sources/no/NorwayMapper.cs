using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Norway;

/// <summary>
/// The Norwegian mapper (PRD §4.4, plan R8): counties (KLASS 104) and municipalities (KLASS 131) as places keyed on
/// (code, valid from), so a recode (Kåfjord 1940 → 5426 → 5540) keeps the place and adds a dated code, a merge under a
/// new code (Larvik 0709 + Lardal 0728 → 0712) creates a new place, and a split (Troms og Finnmark 54 → 55 + 56) ends
/// the old one with part edges. Names arrive with Sami and Kven forms joined by " - " and are split (see
/// <see cref="NorwayNames"/>). Streets and postcode links come from Kartverket's address points, postcodes and their
/// localities from Bring.
/// </summary>
internal sealed partial class NorwayMapper : IReferenceDataMapper
{
    public const string Zone = "Europe/Oslo";

    [GeneratedRegex(@"(?i)^postnummerregister.*\.txt$")]
    private static partial Regex PostcodeFile();

    public IReadOnlyList<string> Countries => ["no"];
    public IReadOnlyList<string> SourceIds => [NorwayKlassSource.SourceId, NorwayAddressSource.SourceId, NorwayPostcodeSource.SourceId];
    public int Order => 10;
    public bool IsCountryAdapter => true;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(NorwayMapper).Assembly);

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        var run = new Run(build);
        run.MapCountry();

        // The Norwegian name of each current municipality, as the address register writes it (KÅFJORD): it tells
        // which part of a joined Sami/Norwegian/Kven name is the Norwegian one.
        var addressRows = new List<RefCsvRow>();
        if (inputs.TryGetValue(NorwayAddressSource.SourceId, out var addresses))
            foreach (var file in addresses.Files.Where(f => NorwayAddressSource.AddressFile().IsMatch(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal))
                addressRows.AddRange(RefCsv.ReadRecords(file.Open(), ';'));
        var postcodeRows = new List<string[]>();
        if (inputs.TryGetValue(NorwayPostcodeSource.SourceId, out var postcodes) && postcodes.Find(PostcodeFile()) is { } postcodeFile)
            postcodeRows.AddRange(RefCsv.Read(postcodeFile.Open(), '\t', RefCsv.Latin1, quotes: false).Where(r => r.Length >= 5));
        foreach (var row in addressRows)
            run.NorwegianNames.TryAdd(row["kommunenummer"], row["kommunenavn"]);
        foreach (var row in postcodeRows)
            run.NorwegianNames.TryAdd(row[2], row[3]);

        if (inputs.TryGetValue(NorwayKlassSource.SourceId, out var klass))
        {
            run.MapClassification(Read(klass, "104"), "fylke", "no:county", PlaceRole.Region, parentOf: null);
            run.MapClassification(Read(klass, "131"), "knr", "no:municipality", PlaceRole.Municipality, parentOf: code => code[..2]);
        }
        run.MapAddresses(addressRows);
        run.MapPostcodes(postcodeRows);
        return Task.CompletedTask;
    }

    private static KlassData Read(SourceInput input, string classification)
    {
        var data = new KlassData();
        foreach (var file in input.Files.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var match = NorwayKlassSource.FilePattern().Match(file.Name);
            if (!match.Success || match.Groups["classification"].Value != classification) continue;
            var from = DateOnly.ParseExact(match.Groups["from"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            DateOnly? to = match.Groups["to"].Value == "open" ? null : DateOnly.ParseExact(match.Groups["to"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            using var stream = file.Open();
            if (match.Groups["kind"].Value == "codes")
            {
                data.RequestedFrom = from;
                data.RequestedTo = to;
                data.Codes.AddRange(JsonSerializer.Deserialize<KlassCodes>(stream)?.Codes ?? []);
            }
            else
                data.Changes.AddRange(JsonSerializer.Deserialize<KlassChanges>(stream)?.CodeChanges ?? []);
        }
        return data;
    }

    internal sealed class KlassData
    {
        public DateOnly RequestedFrom { get; set; }
        public DateOnly? RequestedTo { get; set; }
        public List<KlassCode> Codes { get; } = [];
        public List<KlassChange> Changes { get; } = [];
    }

    internal sealed record KlassCodes([property: JsonPropertyName("codes")] List<KlassCode>? Codes);
    internal sealed record KlassCode(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("validFromInRequestedRange")] string? From,
        [property: JsonPropertyName("validToInRequestedRange")] string? To);
    internal sealed record KlassChanges([property: JsonPropertyName("codeChanges")] List<KlassChange>? CodeChanges);
    internal sealed record KlassChange(
        [property: JsonPropertyName("oldCode")] string OldCode,
        [property: JsonPropertyName("newCode")] string NewCode,
        [property: JsonPropertyName("changeOccurred")] string ChangeOccurred);

    /// <summary>One continuous life of a code: its name versions, back to back.</summary>
    private sealed record Incarnation(string Code, DateOnly? From, DateOnly? To, List<(string Name, DateOnly? From, DateOnly? To)> Names);

    private sealed class Run(RefDataBuild build)
    {
        private string countryPlace = string.Empty;

        /// <summary>(scheme, code) → the places that held the code, with the period they held it.</summary>
        private readonly Dictionary<(string Scheme, string Code), List<(string Place, DateOnly? From, DateOnly? To)>> holders = [];

        /// <summary>Plain ids already minted in this build: a second unit with the same code gets the <c>@date</c> suffix.</summary>
        private readonly HashSet<string> mintedPlain = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> NorwegianNames { get; } = new(StringComparer.Ordinal);

        public void MapCountry()
        {
            Country(build, "no", "NOR", "578", NorwayProfile.Create(), NorwayKlassSource.SourceId);
            countryPlace = CountryPlace(build, "no", NorwayKlassSource.SourceId, Zone);
            // CLDR's subset carries de/en/fr/nl only; the endonym is the country's own (hand-authored like the profile).
            var place = build.Place(countryPlace);
            if (!place.Names.Entries.Any(e => e.Tag == "no"))
                place.Names.Entries.Insert(0, Name("no", "Norge", TranslatedStringKind.Official, "profile"));
            place.OfficialLanguages = [new() { Lang = "no" }];
        }

        public void MapClassification(KlassData data, string scheme, string kind, PlaceRole role, Func<string, string>? parentOf)
        {
            var incarnations = Incarnations(data);
            var changes = data.Changes
                .Where(c => c.OldCode != c.NewCode) // the same code under a new name is a rename: the name versions carry it
                .Select(c => (c.OldCode, c.NewCode, Date: DateOnly.ParseExact(c.ChangeOccurred, "yyyy-MM-dd", CultureInfo.InvariantCulture)))
                .Distinct()
                .ToList();

            // A recode: one old code → one new code on a date, with no other participant on either side.
            var recodes = changes
                .Where(c => changes.Count(x => x.Date == c.Date && x.NewCode == c.NewCode) == 1
                         && changes.Count(x => x.Date == c.Date && x.OldCode == c.OldCode) == 1)
                .ToDictionary(c => (c.NewCode, c.Date), c => c.OldCode);

            var placeOf = new Dictionary<Incarnation, string>();
            foreach (var inc in incarnations.OrderBy(i => DateKeys.From(i.From)).ThenBy(i => i.Code, StringComparer.Ordinal))
            {
                var identity = inc.From is { } f && f > data.RequestedFrom ? $"{scheme}:{inc.Code}@{f:yyyy-MM-dd}" : $"{scheme}:{inc.Code}";
                string id;
                if (inc.From is { } start && recodes.TryGetValue((inc.Code, start), out var oldCode)
                    && incarnations.FirstOrDefault(o => o.Code == oldCode && o.To == start) is { } old && placeOf.TryGetValue(old, out var continued))
                {
                    id = build.Ledger.Alias(NorwayKlassSource.SourceId, identity, continued);
                }
                else
                {
                    var plain = RefIds.Place("no", scheme, inc.Code);
                    var rule = mintedPlain.Add(plain) ? plain : RefIds.Place("no", scheme, inc.Code, inc.From);
                    id = build.Ledger.Mint(NorwayKlassSource.SourceId, identity, rule);
                }
                placeOf[inc] = id;
                if (!holders.TryGetValue((scheme, inc.Code), out var list)) holders[(scheme, inc.Code)] = list = [];
                list.Add((id, inc.From, inc.To));
            }

            // The Norwegian name of a place, from any code it held (the address register knows only today's code).
            string? NorwegianOf(string id) => placeOf.Where(p => p.Value == id).Select(p => NorwegianNames.GetValueOrDefault(p.Key.Code)).FirstOrDefault(n => n is not null);

            foreach (var (inc, id) in placeOf)
            {
                var place = build.Place(id);
                AddSource(place.Source, NorwayKlassSource.SourceId);
                place.TimeZone = Zone;
                place.Codes.Add(new PlaceCode { Scheme = scheme, Value = inc.Code, From = inc.From, To = inc.To });
                if (!place.OfficialLanguages.Any(l => l.Lang == "no")) place.OfficialLanguages.Add(new DatedLanguage { Lang = "no" });
                foreach (var (name, from, to) in inc.Names)
                {
                    var entries = NorwayNames.Split(name, NorwegianOf(id), NorwayKlassSource.SourceId, from, to, scheme == "fylke").ToList();
                    if (entries.Count == 1 && name.Contains(" - ", StringComparison.Ordinal))
                        build.Warnings.Add($"{NorwayKlassSource.SourceId}: '{name}' ({inc.Code}) kept whole: no Norwegian name known for it.");
                    foreach (var entry in entries)
                    {
                        // A name that carries on into the next version (or the next code) extends its entry.
                        var previous = place.Names.Entries.FindIndex(e => e.Tag == entry.Tag && e.Value == entry.Value && e.ValidTo is not null && e.ValidTo == entry.ValidFrom);
                        if (previous >= 0)
                            place.Names.Entries[previous] = place.Names.Entries[previous] with { ValidTo = entry.ValidTo };
                        else
                            place.Names.Entries.Add(entry);
                    }
                }

                foreach (var (from, to, parent) in ParentsOf(inc, parentOf))
                    place.Periods.Add(new PlacePeriod { From = from, To = to, Kind = kind, Role = role, Parent = parent });
            }

            foreach (var place in placeOf.Values.Distinct().Select(build.Place))
                place.Periods = Merge(place.Periods);

            // Lineage from the change tables (Q-R3: SSB is the primary source; its date drives the periods).
            foreach (var group in changes.GroupBy(c => (c.NewCode, c.Date)).OrderBy(g => g.Key.Date).ThenBy(g => g.Key.NewCode, StringComparer.Ordinal))
            {
                var (newCode, date) = group.Key;
                // A target of a split (one old code → several new ones) is handled with the split below.
                if (group.Count() == 1 && changes.Count(x => x.Date == date && x.OldCode == group.First().OldCode) > 1)
                    continue;
                var target = HolderAt(scheme, newCode, date);
                var olds = group.Select(g => HolderEndingAt(scheme, g.OldCode, date)).ToList();
                if (target is null || olds.Any(o => o is null))
                {
                    build.Warnings.Add($"{NorwayKlassSource.SourceId}: the change {string.Join("+", group.Select(g => g.OldCode))} → {newCode} on {date:yyyy-MM-dd} names a code the version lists do not hold.");
                    continue;
                }
                if (recodes.ContainsKey((newCode, date)))
                {
                    var recode = new RefLineageEvent
                    {
                        Id = RefIds.LineageEvent("no", date, LineageKind.Recode, scheme, newCode),
                        Country = "no", Date = date, Kind = LineageKind.Recode,
                        Operations = [new LineageOperation { Place = target, Op = LineageOp.Recode }],
                        Sources = [new LineageSource { Source = NorwayKlassSource.SourceId, Date = date, Note = $"{group.First().OldCode} → {newCode}" }],
                    };
                    var periods = build.Place(target).Periods;
                    if (periods.FirstOrDefault(p => p.To == date)?.Parent is { } before && periods.FirstOrDefault(p => p.From == date)?.Parent is { } after && before != after)
                        recode.Operations.Add(new LineageOperation { Place = target, Op = LineageOp.Reparent });
                    build.AddLineage(recode);
                    continue;
                }

                // n → 1 under a new code is a merge into a new place.
                var merge = new RefLineageEvent
                {
                    Id = RefIds.LineageEvent("no", date, LineageKind.Merge, scheme, newCode),
                    Country = "no", Date = date, Kind = LineageKind.Merge,
                    Operations = [new LineageOperation { Place = target, Op = LineageOp.Create }],
                    Sources = [new LineageSource { Source = NorwayKlassSource.SourceId, Date = date, Note = $"{string.Join(", ", group.Select(g => g.OldCode).OrderBy(c => c, StringComparer.Ordinal))} → {newCode}" }],
                };
                foreach (var old in olds.Distinct())
                    merge.Operations.Add(new LineageOperation { Place = old!, Op = LineageOp.End, Into = target, Share = SuccessionShare.Whole });
                build.AddLineage(merge);
            }

            // 1 → n: the old unit ends; each part goes to one of the new ones (Troms og Finnmark 54 → 55 + 56).
            foreach (var group in changes.GroupBy(c => (c.OldCode, c.Date)).Where(g => g.Count() > 1).OrderBy(g => g.Key.Date).ThenBy(g => g.Key.OldCode, StringComparer.Ordinal))
            {
                var (oldCode, date) = group.Key;
                if (HolderEndingAt(scheme, oldCode, date) is not { } old) continue;
                var split = new RefLineageEvent
                {
                    Id = RefIds.LineageEvent("no", date, LineageKind.Split, scheme, oldCode),
                    Country = "no", Date = date, Kind = LineageKind.Split,
                    Sources = [new LineageSource { Source = NorwayKlassSource.SourceId, Date = date, Note = $"{oldCode} → {string.Join(", ", group.Select(g => g.NewCode).OrderBy(c => c, StringComparer.Ordinal))}" }],
                };
                foreach (var change in group.OrderBy(g => g.NewCode, StringComparer.Ordinal))
                    if (HolderAt(scheme, change.NewCode, date) is { } part)
                        split.Operations.Add(new LineageOperation { Place = old, Op = LineageOp.End, Into = part, Share = SuccessionShare.Part });
                // A split's targets that took part in a merge the same day are already created by it.
                build.AddLineage(split);
            }
        }

        /// <summary>The code lists cut into incarnations: name versions back to back belong to one unit.</summary>
        private static List<Incarnation> Incarnations(KlassData data)
        {
            DateOnly? From(string? text) => text is null ? null : DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture) is var d && d <= data.RequestedFrom ? null : d;
            DateOnly? To(string? text) => text is null ? null : DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture) is var d && (data.RequestedTo is { } end && d >= end) ? null : d;

            var result = new List<Incarnation>();
            foreach (var group in data.Codes.GroupBy(c => c.Code).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Incarnation? current = null;
                foreach (var version in group.Select(c => (c.Name, From: From(c.From), To: To(c.To))).OrderBy(v => DateKeys.From(v.From)))
                {
                    if (current is not null && current.To == version.From && version.From is not null)
                    {
                        current.Names.Add((version.Name, version.From, version.To));
                        current = current with { To = version.To };
                        result[^1] = current;
                        continue;
                    }
                    current = new Incarnation(group.Key, version.From, version.To, [(version.Name, version.From, version.To)]);
                    result.Add(current);
                }
            }
            return result;
        }

        /// <summary>The parent per stretch of an incarnation: the county holding the code's county prefix over that time.</summary>
        private IEnumerable<(DateOnly? From, DateOnly? To, string? Parent)> ParentsOf(Incarnation inc, Func<string, string>? parentOf)
        {
            if (parentOf is null)
            {
                yield return (inc.From, inc.To, countryPlace);
                yield break;
            }
            // The kommunenummer's first two digits are its county by the classification's own definition (SSB);
            // which county *place* that is follows the county code's own periods.
            var county = parentOf(inc.Code);
            var counties = holders.GetValueOrDefault(("fylke", county)) ?? [];
            var cuts = counties.SelectMany(c => new[] { c.From, c.To }).Where(d => d is not null && (inc.From is null || d > inc.From) && (inc.To is null || d < inc.To))
                .Distinct().OrderBy(d => d).ToList();
            var bounds = new List<DateOnly?> { inc.From };
            bounds.AddRange(cuts);
            bounds.Add(inc.To);
            for (var i = 0; i < bounds.Count - 1; i++)
            {
                var at = bounds[i];
                var holder = counties.FirstOrDefault(c => (c.From is null || (at is not null && c.From <= at)) && (c.To is null || (at is not null && at < c.To)));
                if (holder.Place is null)
                    build.Warnings.Add($"{NorwayKlassSource.SourceId}: no county {county} for municipality {inc.Code} from {at?.ToString("yyyy-MM-dd") ?? "the start"}.");
                yield return (at, bounds[i + 1], holder.Place ?? countryPlace);
            }
        }

        private static List<PlacePeriod> Merge(List<PlacePeriod> periods)
        {
            var ordered = periods.OrderBy(p => DateKeys.From(p.From)).ToList();
            var merged = new List<PlacePeriod>();
            foreach (var period in ordered)
            {
                if (merged.Count > 0 && merged[^1].To == period.From && period.From is not null && merged[^1].Parent == period.Parent && merged[^1].Kind == period.Kind)
                    merged[^1].To = period.To;
                else
                    merged.Add(period);
            }
            return merged;
        }

        private string? HolderAt(string scheme, string code, DateOnly day)
            => holders.GetValueOrDefault((scheme, code))?.FirstOrDefault(h => (h.From is null || h.From <= day) && (h.To is null || day < h.To)).Place;

        private string? HolderEndingAt(string scheme, string code, DateOnly day)
            => holders.GetValueOrDefault((scheme, code))?.FirstOrDefault(h => h.To == day).Place;

        /// <summary>The place holding a municipality code today (the address and postcode files are current).</summary>
        private string? CurrentMunicipality(string code)
            => holders.GetValueOrDefault(("knr", code))?.FirstOrDefault(h => h.To is null).Place;

        public void MapAddresses(List<RefCsvRow> rows)
        {
            foreach (var row in rows.Where(r => r["adressetype"] == "vegadresse" && r["adressekode"].Length > 0))
            {
                var municipalityCode = row["kommunenummer"];
                if (CurrentMunicipality(municipalityCode) is not { } municipality)
                {
                    build.Warnings.Add($"{NorwayAddressSource.SourceId}: municipality {municipalityCode} is not in the KLASS lists.");
                    continue;
                }
                // The street keeps the code space of the municipality's place (its first kommunenummer), so a recode
                // of the municipality does not re-identify its streets.
                var placeCode = municipality[(municipality.LastIndexOf('/') + 1)..].Replace('@', '-');
                var streetId = build.Ledger.Mint(NorwayAddressSource.SourceId, $"adressekode:{placeCode}-{row["adressekode"]}", RefIds.Street("no", "k" + placeCode, row["adressekode"]));
                var street = build.Street(streetId, "no");
                if (street.Names.Entries.Count == 0)
                {
                    street.Names.Entries.Add(Name(NorwayNames.LanguageOfStreet(row["adressenavn"]), row["adressenavn"], TranslatedStringKind.Official, NorwayAddressSource.SourceId));
                    street.Codes = [new PlaceCode { Scheme = "adressekode", Value = $"{municipalityCode}-{row["adressekode"]}" }];
                    street.Type = StreetType.Streetname;
                }
                AddSource(street.Source, NorwayAddressSource.SourceId);

                string? postalId = null;
                if (row["postnummer"].Length == 4)
                {
                    postalId = build.Ledger.Mint(NorwayAddressSource.SourceId, "postnummer:" + row["postnummer"], RefIds.PostalCode("no", row["postnummer"]));
                    var postal = build.PostalCode(postalId, "no", row["postnummer"]);
                    Locality(postal, TitleCase(row["poststed"]), "no", NorwayAddressSource.SourceId, municipality, main: true);
                }
                StreetIn(street, municipality, postalId);
            }
        }

        public void MapPostcodes(List<string[]> rows)
        {
            foreach (var row in rows)
            {
                var (code, name, municipalityCode, category) = (row[0].Trim(), row[1].Trim(), row[2].Trim(), row[4].Trim());
                if (category == "S" || code.Length != 4) continue; // service codes are not addresses
                var municipality = CurrentMunicipality(municipalityCode);
                if (municipality is null)
                    build.Warnings.Add($"{NorwayPostcodeSource.SourceId}: postcode {code} names municipality {municipalityCode}, which the KLASS lists do not hold.");
                var id = build.Ledger.Mint(NorwayPostcodeSource.SourceId, "postnummer:" + code, RefIds.PostalCode("no", code));
                var postal = build.PostalCode(id, "no", code);
                Locality(postal, TitleCase(name), "no", NorwayPostcodeSource.SourceId, municipality, main: true);
            }
        }
    }

    /// <summary>"OLDERDALEN" → "Olderdalen", "MO I RANA" → "Mo i Rana": the registers write place names in capitals.</summary>
    internal static string TitleCase(string upper)
    {
        var lower = upper.ToLowerInvariant();
        var builder = new StringBuilder(lower.Length);
        for (var i = 0; i < lower.Length; i++)
        {
            var startOfWord = i == 0 || lower[i - 1] is ' ' or '-';
            var word = lower[i..].Split(' ', '-')[0];
            builder.Append(startOfWord && word is not ("i" or "på" or "og") ? char.ToUpperInvariant(lower[i]) : lower[i]);
        }
        return builder.ToString();
    }
}

/// <summary>
/// Norwegian, Sami and Kven names arrive joined in one string ("Gáivuotna - Kåfjord - Kaivuono"; PRD §4.4) and are
/// split into one entry per language. SSB's order is not fixed (Oslo puts Norwegian first, Kåfjord Sami first), and the
/// language of each part is not published with the code (SSR keeps it per name; SSR is not imported). The rule:
/// <list type="number">
/// <item>The Norwegian part is the one equal (folded) to the name the address register or Bring writes for the
/// municipality (KÅFJORD); for counties it is the first part (SSB writes county names Norwegian first).</item>
/// <item>A part with a Northern Sami letter (á č đ ŋ š ŧ ž) is Northern Sami (<c>se</c>).</item>
/// <item>Of the parts left, in a three-part name the last is Kven (<c>fkv</c>); any other is Sami without a stated
/// variety (<c>smi</c>: "Oslove" is South Sami, "Guovdageaidnu" Northern Sami without a special letter).</item>
/// </list>
/// When the Norwegian part cannot be identified, the name is kept whole as Norwegian and the build warns. ⚠️ A
/// heuristic until SSR's per-name languages are imported (RS8).
/// </summary>
internal static class NorwayNames
{
    private const string SamiLetters = "áčđŋšŧžÁČĐŊŠŦŽ";

    public static IEnumerable<TranslatedStringEntry> Split(string joined, string? norwegian, string source, DateOnly? from, DateOnly? to, bool norwegianFirst)
    {
        var parts = joined.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            yield return Name("no", joined, TranslatedStringKind.Official, source, from, to);
            yield break;
        }
        var key = norwegian is null ? null : Key(norwegian);
        var norwegianIndex = Array.FindIndex(parts, p => key is not null && Key(p) == key);
        if (norwegianIndex < 0 && norwegianFirst) norwegianIndex = 0;
        if (norwegianIndex < 0)
        {
            yield return Name("no", joined, TranslatedStringKind.Official, source, from, to);
            yield break;
        }
        var rest = Enumerable.Range(0, parts.Length).Where(i => i != norwegianIndex && !parts[i].Any(c => SamiLetters.Contains(c))).ToList();
        for (var i = 0; i < parts.Length; i++)
        {
            var tag = i == norwegianIndex ? "no"
                : parts[i].Any(c => SamiLetters.Contains(c)) ? "se"
                : parts.Length == 3 && i == rest[^1] ? "fkv"
                : "smi";
            yield return Name(tag, parts[i], TranslatedStringKind.Official, source, from, to);
        }
    }

    /// <summary>A street name's language: Northern Sami letters → <c>se</c>; ä/ö (not Norwegian letters) → Kven; else Norwegian.</summary>
    public static string LanguageOfStreet(string name)
        => name.Any(c => SamiLetters.Contains(c)) ? "se"
            : name.Any(c => c is 'ä' or 'ö' or 'Ä' or 'Ö') ? "fkv"
            : "no";
}
