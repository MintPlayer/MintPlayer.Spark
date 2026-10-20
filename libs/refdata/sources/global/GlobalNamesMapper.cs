using System.Text;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;

namespace MintPlayer.Spark.ReferenceData.Sources.Global;

/// <summary>
/// The global name sources (PRD §4.4 "global baseline"; plan R7): NGA GNS conventional names, UN/LOCODE location codes
/// and conventional names, and the IANA Language Subtag Registry. They add names and cross-ids to places the build
/// already holds, whichever adapter made them, so this mapper runs <b>last</b> (the global mapper runs first, before
/// the country adapters create their units). It mints nothing.
/// <list type="bullet">
/// <item><b>GNS:</b> a feature whose approved (<c>N</c>) name matches one place of its country and level gets that
/// feature's conventional (<c>C</c>) names as exonyms ("Antwerp", "East Flanders"). A name already present is not
/// repeated. Where the place's own names are not in Latin script, the entry is an authoritative romanization (RD7;
/// machine transliterations are never displayed, Q-R11).</item>
/// <item><b>UN/LOCODE:</b> a location whose name matches exactly one municipality of its country (current ones first)
/// gets the code (<c>unlocode BEHSS</c>); a reference row ("Antwerp = Antwerpen") adds its left side as an English
/// exonym. Ambiguous matches are listed, never guessed.</item>
/// <item><b>IANA subtags:</b> every language tag the build uses (names, official languages, profiles) and every country
/// code is checked against the registry; unknown or deprecated subtags are listed with their preferred value.</item>
/// </list>
/// </summary>
internal sealed partial class GlobalNamesMapper : IReferenceDataMapper
{
    [GeneratedRegex(@"^(unlocode-codelist\.cut|UNLOCODE ?CodeListPart\d)\.csv$")]
    private static partial Regex CodeListFile();

    [GeneratedRegex(@"^(unlocode-subdivisions\.cut|SubdivisionCodes)\.csv$")]
    private static partial Regex SubdivisionFile();

    [GeneratedRegex(@"^(?<name>[^(]+?)\s*\((?<alt>[^)]+)\)\s*$")]
    private static partial Regex Parenthesised();

    /// <summary>GNS country codes (FIPS 10-4 in the classic files, GENC trigraphs in the new ones) of the covered countries.</summary>
    private static readonly Dictionary<string, string> GnsCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BE"] = "be", ["BEL"] = "be", ["GM"] = "de", ["DEU"] = "de", ["FR"] = "fr", ["FRA"] = "fr",
        ["LU"] = "lu", ["LUX"] = "lu", ["NL"] = "nl", ["NLD"] = "nl",
    };

    /// <summary>GNS language codes (ISO 639-2/B) to RFC 5646 tags.</summary>
    private static readonly Dictionary<string, string> GnsLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "en", ["fre"] = "fr", ["ger"] = "de", ["dut"] = "nl", ["ltz"] = "lb", ["wln"] = "wa",
    };

    public IReadOnlyList<string> Countries => ["be", "de", "fr", "lu", "nl"];

    public IReadOnlyList<string> SourceIds => [GnsSource.SourceId, UnLocodeSource.SourceId, IanaSubtagSource.SourceId];

    /// <summary>After every country adapter (they run at 10).</summary>
    public int Order => 90;

    public IEnumerable<LedgerEntry> ShippedLedger() => [];

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        if (inputs.TryGetValue(GnsSource.SourceId, out var gns))
            foreach (var file in gns.Files.OrderBy(f => f.Name, StringComparer.Ordinal))
                MapGns(build, file);
        if (inputs.TryGetValue(UnLocodeSource.SourceId, out var locode))
            MapUnLocode(build, locode);
        if (inputs.TryGetValue(IanaSubtagSource.SourceId, out var iana) && iana.Files.FirstOrDefault() is { } registryFile)
        {
            using var stream = registryFile.Open();
            Validate(build, IanaSubtags.Read(stream));
        }
        return Task.CompletedTask;
    }

    // ---- GNS ----

    private static void MapGns(RefDataBuild build, SourceFile file)
    {
        using var stream = file.Open();
        var rows = RefCsv.ReadRecords(stream, '\t', Encoding.UTF8, quotes: false).ToList();
        foreach (var feature in rows.GroupBy(r => r["ufi"], StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var approved = feature.FirstOrDefault(r => r["nt"] == "N");
            if (approved is null || !GnsCountries.TryGetValue(approved["cc_ft"], out var country))
                continue;
            var roles = RolesOf(approved["desig_cd"]);
            if (roles.Length == 0)
                continue;
            var candidates = Places(build, country, roles, approved["full_name"]).ToList();
            if (candidates.Count != 1)
            {
                if (candidates.Count > 1)
                    build.Warnings.Add($"global-gns: '{approved["full_name"]}' ({approved["desig_cd"]}, {country}) matches {candidates.Count} places; skipped.");
                continue;
            }
            var place = candidates[0];
            var romanized = place.Names.Entries.Any(e => e.Kind == TranslatedStringKind.Official && !IsLatin(e.Value));
            var added = false;
            foreach (var conventional in feature.Where(r => r["nt"] == "C"))
            {
                var lang = GnsLanguages.GetValueOrDefault(conventional["lang_cd"], "en");
                var value = conventional["full_name"];
                if (value.Length == 0 || place.Names.Entries.Any(e => RefAdapterKit.Key(e.Value) == RefAdapterKit.Key(value)))
                    continue;
                place.Names.Entries.Add(RefAdapterKit.Name(lang, value, TranslatedStringKind.Exonym, GnsSource.SourceId) with
                {
                    Romanization = romanized ? RomanizationOrigin.Authoritative : null,
                });
                added = true;
            }
            if (added)
                RefAdapterKit.AddSource(place.Source, GnsSource.SourceId);
        }
    }

    /// <summary>GNS designations to the generic roles of a place: first- and second-order divisions, or settlements.</summary>
    private static PlaceRole[] RolesOf(string designation) => designation switch
    {
        "ADM1" or "ADM1H" or "ADM2" or "ADM2H" => [PlaceRole.Region, PlaceRole.Subregion],
        "ADM3" or "ADM4" or "PPL" or "PPLA" or "PPLA2" or "PPLA3" or "PPLA4" or "PPLC" => [PlaceRole.Municipality, PlaceRole.Submunicipality, PlaceRole.Locality],
        _ => [],
    };

    // ---- UN/LOCODE ----

    private static void MapUnLocode(RefDataBuild build, SourceInput input)
    {
        var subdivisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in input.Files.Where(f => SubdivisionFile().IsMatch(f.Name)))
        {
            using var stream = file.Open();
            foreach (var f in RefCsv.Read(stream, ',', Encoding.UTF8))
                if (f.Length >= 2) subdivisions.Add($"{f[0]}-{f[1]}");
        }

        foreach (var file in input.Files.Where(f => CodeListFile().IsMatch(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            using var stream = file.Open();
            foreach (var f in RefCsv.Read(stream, ',', Encoding.UTF8))
            {
                if (f.Length < 6 || !RefIds.IsCountryCode(f[1]))
                    continue;
                var country = f[1].ToLowerInvariant();
                if (!build.Countries.ContainsKey(RefIds.CountryId(country)))
                    continue;
                var change = f[0].Trim();
                var location = f[2].Trim();
                var name = f[3].Trim();
                if (change == "X" || name.StartsWith('.'))
                    continue;                                   // marked for removal; the country's own header row
                if (change == "=" || location.Length == 0)
                {
                    // A reference: "Antwerp = Antwerpen" (a conventional English name of a location).
                    var parts = name.Split(" = ", 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2 && Municipality(build, country, [parts[1]], null) is { } referenced
                        && !referenced.Names.Entries.Any(e => RefAdapterKit.Key(e.Value) == RefAdapterKit.Key(parts[0])))
                    {
                        referenced.Names.Entries.Add(RefAdapterKit.Name("en", parts[0], TranslatedStringKind.Exonym, UnLocodeSource.SourceId));
                        RefAdapterKit.AddSource(referenced.Source, UnLocodeSource.SourceId);
                    }
                    continue;
                }
                if (location.Length != 3)
                    continue;
                var names = new List<string> { name };
                if (Parenthesised().Match(name) is { Success: true } m)
                    names = [m.Groups["name"].Value, m.Groups["alt"].Value];
                var subdivision = f[5].Trim() is { Length: > 0 } s && subdivisions.Contains($"{f[1]}-{s}") ? $"{f[1].ToUpperInvariant()}-{s}" : null;
                if (Municipality(build, country, names, subdivision) is not { } place)
                    continue;
                var code = f[1].ToUpperInvariant() + location;
                if (!place.Codes.Any(c => c.Scheme == "unlocode" && c.Value == code))
                {
                    place.Codes.Add(new PlaceCode { Scheme = "unlocode", Value = code });
                    RefAdapterKit.AddSource(place.Source, UnLocodeSource.SourceId);
                }
            }
        }
    }

    /// <summary>The one municipality named so (current ones first; then an ended one, the same town under its former unit).</summary>
    private static RefPlace? Municipality(RefDataBuild build, string country, IReadOnlyList<string> names, string? subdivision)
    {
        var all = names.SelectMany(n => Places(build, country, [PlaceRole.Municipality], n)).DistinctBy(p => p.Id).ToList();
        if (subdivision is not null && all.Count > 1)
        {
            var within = all.Where(p => p.Segments.Count == 0 || AncestorCodes(build, p).Contains(subdivision)).ToList();
            if (within.Count > 0) all = within;
        }
        var open = all.Where(p => p.Periods.LastOrDefault()?.To is null).ToList();
        var pick = open.Count > 0 ? open : all;
        if (pick.Count == 1)
            return pick[0];
        if (pick.Count > 1)
            build.Warnings.Add($"global-unlocode: '{string.Join(" / ", names)}' ({country}) matches {pick.Count} municipalities; skipped.");
        return null;
    }

    private static HashSet<string> AncestorCodes(RefDataBuild build, RefPlace place)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parent = place.Periods.LastOrDefault()?.Parent;
        for (var depth = 0; parent is not null && depth < 10 && build.Places.TryGetValue(parent, out var p); depth++)
        {
            foreach (var code in p.Codes.Where(c => c.Scheme == "iso3166-2")) codes.Add(code.Value);
            parent = p.Periods.LastOrDefault()?.Parent;
        }
        return codes;
    }

    /// <summary>Places of <paramref name="country"/> in one of <paramref name="roles"/> (any period) with a name folding to <paramref name="name"/>'s key.</summary>
    private static IEnumerable<RefPlace> Places(RefDataBuild build, string country, PlaceRole[] roles, string name)
    {
        var key = RefAdapterKit.Key(name);
        if (key.Length == 0) return [];
        return build.Places.Values
            .Where(p => string.Equals(p.Country, country, StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Periods.Any(x => roles.Contains(x.Role)))
            .Where(p => p.Names.Entries.Any(e => e.Kind != TranslatedStringKind.Historic && RefAdapterKit.Key(e.Value) == key))
            .OrderBy(p => p.Id, StringComparer.Ordinal);
    }

    private static bool IsLatin(string value)
        => value.All(c => !char.IsLetter(c) || c <= 'ɏ' || c is >= 'Ḁ' and <= 'ỿ');

    // ---- IANA subtags ----

    private static void Validate(RefDataBuild build, IanaSubtags registry)
    {
        var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var place in build.Places.Values)
        {
            foreach (var entry in place.Names.Entries) tags.Add(entry.Tag);
            foreach (var language in place.OfficialLanguages) tags.Add(language.Lang);
        }
        foreach (var street in build.Streets.Values)
            foreach (var entry in street.Names.Entries) tags.Add(entry.Tag);
        foreach (var postal in build.PostalCodes.Values)
            foreach (var locality in postal.Localities)
                foreach (var entry in locality.Names.Entries) tags.Add(entry.Tag);
        foreach (var country in build.Countries.Values)
        {
            foreach (var language in country.Profile.OfficialLanguages) tags.Add(language.Lang);
            if (registry.Problem("region", country.Code) is { } problem)
                build.Warnings.Add($"global-iana-subtags: country {country.Code}: {problem}");
        }
        foreach (var tag in tags)
            foreach (var problem in registry.Check(tag))
                build.Warnings.Add($"global-iana-subtags: tag '{tag}': {problem}");
    }
}

/// <summary>
/// The IANA Language Subtag Registry (RFC 5646 §3.1): records separated by <c>%%</c>, <c>Field: value</c> lines,
/// continuation lines indented. Only what tag validation needs is kept: type, subtag, deprecation and preferred value.
/// </summary>
public sealed class IanaSubtags
{
    private readonly Dictionary<(string Type, string Subtag), (string? Deprecated, string? Preferred)> records = [];

    public string? FileDate { get; private set; }

    public int Count => records.Count;

    /// <summary>The <c>File-Date</c> of a registry's first line, or <see langword="null"/>.</summary>
    public static string? ReadFileDate(string firstLine)
        => firstLine.StartsWith("File-Date:", StringComparison.Ordinal) ? firstLine["File-Date:".Length..].Trim() : null;

    public static IanaSubtags Read(Stream stream)
    {
        var registry = new IanaSubtags();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var first = true;
        while (true)
        {
            var line = reader.ReadLine();
            if (line is null || line == "%%")
            {
                if (first) registry.FileDate = fields.GetValueOrDefault("File-Date");
                else if (fields.TryGetValue("Type", out var type) && (fields.GetValueOrDefault("Subtag") ?? fields.GetValueOrDefault("Tag")) is { } subtag)
                    registry.records[(type, subtag.ToLowerInvariant())] = (fields.GetValueOrDefault("Deprecated"), fields.GetValueOrDefault("Preferred-Value"));
                if (line is null) break;
                first = false;
                fields.Clear();
                continue;
            }
            if (line.StartsWith(' ')) continue;                 // a continuation (descriptions, comments)
            var colon = line.IndexOf(':');
            if (colon > 0) fields.TryAdd(line[..colon], line[(colon + 1)..].Trim());
        }
        return registry;
    }

    /// <summary>What is wrong with a subtag of <paramref name="type"/>, or <see langword="null"/>.</summary>
    public string? Problem(string type, string subtag)
    {
        if (!records.TryGetValue((type, subtag.ToLowerInvariant()), out var record))
            return $"{type} subtag '{subtag}' is not registered";
        if (record.Deprecated is { } since)
            return $"{type} subtag '{subtag}' is deprecated since {since}" + (record.Preferred is { } p ? $" (use '{p}')" : string.Empty);
        return null;
    }

    /// <summary>Checks a tag's language, script and region subtags (extensions and private use are not checked).</summary>
    public IEnumerable<string> Check(string tag)
    {
        var parts = tag.Split('-');
        if (parts[0].Length is < 2 or > 3)
        {
            yield return "the primary language subtag is not 2 or 3 letters";
            yield break;
        }
        if (Problem("language", parts[0]) is { } language) yield return language;
        foreach (var part in parts.Skip(1))
        {
            if (part.Length == 1) yield break;                  // an extension or x- private use: the rest is not ours to check
            if (part.Length == 4 && part.All(char.IsAsciiLetter) && Problem("script", part) is { } script) yield return script;
            if ((part.Length == 2 && part.All(char.IsAsciiLetter) || part.Length == 3 && part.All(char.IsAsciiDigit)) && Problem("region", part) is { } region) yield return region;
        }
    }
}
