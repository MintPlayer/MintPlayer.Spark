using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;

namespace MintPlayer.Spark.ReferenceData.Sources;

/// <summary>
/// A source with one download URL and real sample fixtures embedded in its adapter assembly (PRD §4.4, RS8). The change
/// check is the conditional <c>ETag</c>/<c>Last-Modified</c> request; the fetch downloads the file and, for a ZIP,
/// extracts the entries matching <see cref="EntryPattern"/> (matched by pattern, never by name: dated and renamed
/// inner files keep working). Adapters whose publisher sends no validator override <see cref="CheckAsync"/>.
/// </summary>
public abstract class RefFileSource : IReferenceDataSource
{
    public abstract string Id { get; }
    public abstract string Country { get; }
    public abstract SourceLicence Licence { get; }
    public abstract IReadOnlyList<string> DownloadHosts { get; }
    public virtual string DefaultSchedule => "0 0 5 * * 0";

    /// <summary>The file the change check and the fetch read.</summary>
    protected abstract string DownloadUrl { get; }

    /// <summary>The entries kept from a ZIP download; <see langword="null"/> keeps the download as one file.</summary>
    protected virtual Regex? EntryPattern => null;

    /// <summary>The download cap (bytes).</summary>
    protected virtual long MaxBytes => 4L * 1024 * 1024 * 1024;

    /// <summary>The embedded fixture names (resource names under <c>Fixtures/</c>) this source's fixture input holds.</summary>
    protected abstract IReadOnlyList<string> FixtureNames { get; }

    public virtual Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
        => RefDownloads.CheckConditionalAsync(http, DownloadUrl, last, cancellationToken);

    public virtual async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var url = string.IsNullOrEmpty(version.Url) ? DownloadUrl : version.Url;
        var target = Path.Combine(workDirectory.FullName, RefZip.SafeName(Path.GetFileName(new Uri(url).LocalPath), "download"));
        var sha = await RefDownloads.DownloadAsync(http, url, target, MaxBytes, cancellationToken);
        var files = EntryPattern is { } pattern
            ? RefZip.Extract(target, pattern, workDirectory, MaxBytes)
            : [new SourceFile(Path.GetFileName(target), () => File.OpenRead(target))];
        return new SourceInput { SourceId = Id, Version = version.Version, Url = url, Sha256 = sha, Files = files };
    }

    public SourceInput Fixture() => new()
    {
        SourceId = Id,
        Version = "fixture",
        Files = [.. EmbeddedSourceFiles.Under(GetType().Assembly, "Fixtures/").Where(f => FixtureNames.Contains(f.Name, StringComparer.Ordinal))],
    };
}

/// <summary>ZIP helpers for adapters: extraction by pattern with a size cap and safe file names (downloads are data).</summary>
public static class RefZip
{
    /// <summary>Extracts the entries of <paramref name="zipPath"/> whose file name matches <paramref name="pattern"/>, each capped at <paramref name="maxEntryBytes"/>.</summary>
    public static IReadOnlyList<SourceFile> Extract(string zipPath, Regex pattern, DirectoryInfo workDirectory, long maxEntryBytes)
    {
        var files = new List<SourceFile>();
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries.Where(e => e.Length > 0 && pattern.IsMatch(e.Name)).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            if (entry.Length > maxEntryBytes)
                throw new InvalidDataException($"{entry.FullName} is larger than the {maxEntryBytes}-byte cap.");
            var path = Path.Combine(workDirectory.FullName, SafeName(entry.Name, "entry"));
            entry.ExtractToFile(path, overwrite: true);
            files.Add(new SourceFile(Path.GetFileName(path), () => File.OpenRead(path)));
        }
        return files;
    }

    /// <summary>A file name stripped of path parts and characters a file system refuses (zip-slip safe).</summary>
    public static string SafeName(string name, string fallback)
    {
        var file = Path.GetFileName(name.Replace('\\', '/').Split('/').Last());
        var clean = new string([.. file.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')]);
        return clean.Trim('.').Length == 0 ? fallback : clean;
    }
}

/// <summary>
/// A streaming reader for the delimited files registers publish (CSV, semicolon and pipe files, TSV). Quotes follow
/// RFC 4180 (a quoted field may hold the delimiter, doubled quotes and line breaks). A BOM is honoured; otherwise
/// <c>encoding</c> applies (cp1252 needs <see cref="Latin1"/> or a registered code page).
/// </summary>
public static class RefCsv
{
    /// <summary>ISO-8859-1, built in on every platform; a stand-in for files declared Windows-1252 whose bytes stay below 0x80–0x9F.</summary>
    public static Encoding Latin1 => Encoding.Latin1;

    /// <summary>Every record of <paramref name="stream"/> as fields, header included.</summary>
    public static IEnumerable<string[]> Read(Stream stream, char delimiter, Encoding? encoding = null, bool quotes = true)
    {
        using var reader = new StreamReader(stream, encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var any = false;
        int read;
        while ((read = reader.Read()) >= 0)
        {
            var c = (char)read;
            any = true;
            if (quoted)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                    else quoted = false;
                }
                else field.Append(c);
                continue;
            }
            if (quotes && c == '"' && field.Length == 0) { quoted = true; continue; }
            if (c == delimiter) { fields.Add(field.ToString()); field.Clear(); continue; }
            if (c == '\r') continue;
            if (c == '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                if (!(fields.Count == 1 && fields[0].Length == 0))
                    yield return [.. fields];
                fields.Clear();
                any = false;
                continue;
            }
            field.Append(c);
        }
        if (any)
        {
            fields.Add(field.ToString());
            if (!(fields.Count == 1 && fields[0].Length == 0))
                yield return [.. fields];
        }
    }

    /// <summary>Records keyed by the header row (column names compared ignoring case).</summary>
    public static IEnumerable<RefCsvRow> ReadRecords(Stream stream, char delimiter, Encoding? encoding = null, bool quotes = true)
    {
        Dictionary<string, int>? header = null;
        foreach (var fields in Read(stream, delimiter, encoding, quotes))
        {
            if (header is null)
            {
                header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < fields.Length; i++)
                    header.TryAdd(fields[i].Trim().TrimStart('﻿'), i);
                continue;
            }
            yield return new RefCsvRow(header, fields);
        }
    }
}

/// <summary>One record of <see cref="RefCsv.ReadRecords"/>.</summary>
public sealed class RefCsvRow(IReadOnlyDictionary<string, int> header, string[] fields)
{
    public IReadOnlyList<string> Fields => fields;

    /// <summary>The trimmed value of <paramref name="column"/>, or an empty string when the column or value is absent.</summary>
    public string this[string column] => header.TryGetValue(column, out var i) && i < fields.Length ? fields[i].Trim() : string.Empty;

    public bool Has(string column) => header.ContainsKey(column);
}

/// <summary>Building blocks every tier-1 adapter's mapper shares (PRD §4.2–§4.4).</summary>
public static class RefAdapterKit
{
    /// <summary>The compact search key of a name: <c>Sint-Martens-Latem</c> → <c>sintmartenslatem</c> (postal-locality keys).</summary>
    public static string Key(string name) => string.Concat(RefSearchKeys.Tokenize(RefSearchKeys.Fold(name)));

    /// <summary>
    /// The country itself as a place (the root of every ancestor chain), minted as <c>iso3166:{CC}</c> →
    /// <c>places/{cc}/iso3166/{CC}</c> whichever adapter sees it first. Names are CLDR's (RD-DEF 10): official in the
    /// official languages, exonyms in de/en/fr/nl otherwise.
    /// </summary>
    public static string CountryPlace(RefDataBuild build, string country, string source, string? timeZone, params string[] officialLanguages)
    {
        var cc = RefIds.Country(country);
        var upper = cc.ToUpperInvariant();
        var id = build.Ledger.Mint(source, "iso3166:" + upper, RefIds.Place(cc, "iso3166", upper));
        var place = build.Place(id);
        if (place.Periods.Count == 0)
        {
            place.Periods = [new PlacePeriod { Kind = cc + ":country", Role = PlaceRole.Country }];
            place.Names = new TranslatedString();
            // The pinned CLDR subset names de/en/fr/nl only: an endonym in another official language is the adapter's to add.
            foreach (var lang in officialLanguages.Where(l => l is "de" or "en" or "fr" or "nl"))
                place.Names.Entries.Add(new TranslatedStringEntry(lang, CldrNames.Territory(upper, lang)) { Kind = TranslatedStringKind.Official, Source = "cldr" });
            foreach (var lang in new[] { "de", "en", "fr", "nl" }.Where(l => !officialLanguages.Contains(l)))
                place.Names.Entries.Add(new TranslatedStringEntry(lang, CldrNames.Territory(upper, lang)) { Kind = TranslatedStringKind.Exonym, Source = "cldr" });
            place.OfficialLanguages = [.. officialLanguages.Select(l => new DatedLanguage { Lang = l })];
            place.Codes = [new PlaceCode { Scheme = "iso3166", Value = upper }];
            place.TimeZone = timeZone;
            if (timeZone is null)
                place.TimeZoneNote = "The country has several zones: its places carry their own.";
            place.Source = ["cldr"];
        }
        build.Country(cc).Place = id;
        return id;
    }

    /// <summary>Writes the country document's profile and codes (the adapter's profile replaces the baseline's).</summary>
    public static RefCountry Country(RefDataBuild build, string country, string alpha3, string numeric, CountryProfile profile, string source)
    {
        var document = build.Country(country);
        document.Alpha3 = alpha3;
        document.Numeric = numeric;
        document.Profile = profile;
        if (!document.Source.Contains(source)) document.Source.Add(source);
        return document;
    }

    /// <summary>An administrative level of a hand-authored profile (RD-DEF 6).</summary>
    public static AdminLevel Level(string key, PlaceRole role, int order, int rankWeight, bool optional, params (string Tag, string Label)[] labels) => new()
    {
        Key = key,
        Role = role,
        Order = order,
        Optional = optional,
        RankWeight = rankWeight,
        Labels = TranslatedString.Of(labels),
    };

    /// <summary>A name entry with its kind, validity and source.</summary>
    public static TranslatedStringEntry Name(string lang, string value, TranslatedStringKind kind, string source, DateOnly? from = null, DateOnly? to = null)
        => new(lang, value) { Kind = kind, Source = source, ValidFrom = from, ValidTo = to };

    /// <summary>Adds <paramref name="source"/> to a document's source list once.</summary>
    public static void AddSource(List<string> sources, string source)
    {
        if (!sources.Contains(source)) sources.Add(source);
    }

    /// <summary>
    /// The (postal code, name) locality <paramref name="name"/> of <paramref name="postal"/>, created when new, linked to
    /// <paramref name="municipality"/> (open-ended unless dated) and to the locality place where one is known.
    /// </summary>
    public static PostalLocality Locality(RefPostalCode postal, string name, string lang, string source, string? municipality, string? place = null, bool main = false, DateOnly? from = null)
    {
        var key = Key(name);
        var locality = postal.Localities.FirstOrDefault(l => l.Key == key);
        if (locality is null)
        {
            locality = new PostalLocality
            {
                Key = key,
                Names = new TranslatedString { Entries = [Name(lang, name, TranslatedStringKind.Official, source)] },
                Place = place,
                Main = main,
            };
            postal.Localities.Add(locality);
        }
        if (municipality is not null && !locality.Municipalities.Any(m => string.Equals(m.Id, municipality, StringComparison.OrdinalIgnoreCase)))
            locality.Municipalities.Add(new DatedLink { Id = municipality, From = from });
        locality.Place ??= place;
        AddSource(postal.Source, source);
        return locality;
    }

    /// <summary>The street's period in <paramref name="municipality"/>, created when new, with <paramref name="postalCode"/> added.</summary>
    public static StreetPeriod StreetIn(RefStreet street, string municipality, string? postalCode, string? locality = null, DateOnly? from = null, DateOnly? to = null)
    {
        var period = street.Periods.FirstOrDefault(p => string.Equals(p.Municipality, municipality, StringComparison.OrdinalIgnoreCase) && p.From == from);
        if (period is null)
            street.Periods.Add(period = new StreetPeriod { Municipality = municipality, Locality = locality, From = from, To = to });
        if (postalCode is not null && !period.PostalCodes.Contains(postalCode, StringComparer.OrdinalIgnoreCase))
            period.PostalCodes.Add(postalCode);
        period.Locality ??= locality;
        return period;
    }

    /// <summary>Parses a register date in any of the listed exact formats; <see langword="null"/> for an empty or open value.</summary>
    public static DateOnly? Date(string? text, params string[] formats)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        if (value.Length >= 10 && formats.Length == 0 && DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var iso))
            return iso;
        foreach (var format in formats.Length == 0 ? ["yyyy-MM-dd", "dd.MM.yyyy", "dd/MM/yyyy", "yyyyMMdd"] : formats)
            if (DateOnly.TryParseExact(value.Length > format.Length && format.Length >= 8 ? value[..format.Length] : value, format, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day))
                return day;
        return null;
    }

    /// <summary>A date the registers use for "open" (<c>9999-12-31</c> and later) becomes <see langword="null"/>.</summary>
    public static DateOnly? OpenEnd(DateOnly? to) => to is { Year: >= 9999 } ? null : to;
}
