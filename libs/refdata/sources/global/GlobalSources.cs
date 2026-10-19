using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace MintPlayer.Spark.ReferenceData.Sources.Global;

/// <summary>Credit lines (PRD §4.12).</summary>
internal static class GlobalLicences
{
    public static readonly SourceLicence GeoNames = new(
        "GeoNames", "CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/",
        "Contains data from GeoNames (https://www.geonames.org), licensed under CC BY 4.0, modified.");

    public static readonly SourceLicence Wikidata = new(
        "Wikidata", "CC0 1.0", "https://creativecommons.org/publicdomain/zero/1.0/",
        "Labels and codes from Wikidata (https://www.wikidata.org), CC0; credited although CC0 does not require it.", Modified: false);

    public static readonly SourceLicence Cldr = new(
        "Unicode CLDR", "Unicode License v3", "https://www.unicode.org/license.txt",
        "Includes Unicode CLDR data, Copyright © 1991-2026 Unicode, Inc., under the Unicode License v3 (text shipped as CLDR-LICENSE.txt).", Modified: false);

    public static readonly SourceLicence TzData = new(
        "IANA tz database", "Public domain", "https://www.iana.org/time-zones",
        "Zone list from the IANA tz database (zone.tab), public domain.", Modified: false);
}

/// <summary>Countries whose first-level units the baseline imports (the seed set's NL, FR, LU and DE).</summary>
internal static class GlobalCountries
{
    public static readonly string[] Codes = ["DE", "FR", "LU", "NL"];
}

internal abstract class EmbeddedFixtureSource : IReferenceDataSource
{
    public abstract string Id { get; }
    public string Country => "*";
    public abstract SourceLicence Licence { get; }
    public abstract IReadOnlyList<string> DownloadHosts { get; }
    public virtual string DefaultSchedule => "0 0 6 * * 0";
    protected abstract IReadOnlyList<string> FixtureNames { get; }

    public abstract Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken);
    public abstract Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken);

    public SourceInput Fixture() => new()
    {
        SourceId = Id,
        Version = "fixture",
        Files = [.. EmbeddedSourceFiles.Under(typeof(EmbeddedFixtureSource).Assembly, "Fixtures/").Where(f => FixtureNames.Contains(f.Name))],
    };

    protected static SourceInput FromFiles(string id, SourceVersion version, string? sha, params string[] paths) => new()
    {
        SourceId = id,
        Version = version.Version,
        Url = version.Url,
        Sha256 = sha,
        Files = [.. paths.Select(p => new SourceFile(Path.GetFileName(p), () => File.OpenRead(p)))],
    };
}

/// <summary>
/// GeoNames (<c>global-geonames</c>): the country (PCLI) and first-level (ADM1) features of <see cref="GlobalCountries"/>,
/// with population and the per-feature time zone (Q-R10). Reads the per-country dump files, never the web service.
/// </summary>
internal sealed class GeoNamesSource : EmbeddedFixtureSource
{
    public const string SourceId = "global-geonames";
    public override string Id => SourceId;
    public override SourceLicence Licence => GlobalLicences.GeoNames;
    public override IReadOnlyList<string> DownloadHosts => ["download.geonames.org"];
    protected override IReadOnlyList<string> FixtureNames => ["geonames-adm1.cut.txt"];

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        // The per-country files share the dump's modification date; one conditional check stands for all.
        var url = $"https://download.geonames.org/export/dump/{GlobalCountries.Codes[0]}.zip";
        return await RefDownloads.CheckConditionalAsync(http, url, last, cancellationToken);
    }

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var output = Path.Combine(workDirectory.FullName, "geonames-adm1.txt");
        await using var writer = new StreamWriter(output, false, new UTF8Encoding(false)) { NewLine = "\n" };
        foreach (var country in GlobalCountries.Codes)
        {
            var zipPath = Path.Combine(workDirectory.FullName, country + ".zip");
            await RefDownloads.DownloadAsync(http, $"https://download.geonames.org/export/dump/{country}.zip", zipPath, 256L * 1024 * 1024, cancellationToken);
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry(country + ".txt") ?? throw new InvalidDataException($"{country}.zip holds no {country}.txt.");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                var f = line.Split('\t');
                if (f.Length > 7 && f[7] is "ADM1" or "PCLI")
                    await writer.WriteLineAsync(line);
            }
        }
        await writer.FlushAsync(cancellationToken);
        writer.Close();
        return FromFiles(Id, version, null, output);
    }
}

/// <summary>
/// Wikidata (<c>global-wikidata</c>): labels in de/en/fr/nl and ISO 3166-2 codes of the GeoNames units, by GeoNames id
/// (P1566). A SPARQL result has no checksum, so the change check hashes the download (RD1); RS11 decides the method.
/// </summary>
internal sealed class WikidataSource : EmbeddedFixtureSource
{
    public const string SourceId = "global-wikidata";
    public override string Id => SourceId;
    public override SourceLicence Licence => GlobalLicences.Wikidata;
    public override IReadOnlyList<string> DownloadHosts => ["query.wikidata.org"];
    protected override IReadOnlyList<string> FixtureNames => ["wikidata-adm1-labels.csv"];

    private static string Query => """
        SELECT ?gn ?item ?iso ?lang ?label WHERE {
          VALUES ?country { "DE" "FR" "LU" "NL" }
          ?item wdt:P300 ?iso ; wdt:P1566 ?gn .
          FILTER(STRSTARTS(?iso, CONCAT(?country, "-")))
          VALUES ?lang { "en" "nl" "fr" "de" }
          ?item rdfs:label ?label . FILTER(LANG(?label) = ?lang)
        } ORDER BY ?gn ?lang
        """;

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var bytes = await DownloadAsync(http, cancellationToken);
        var sha = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        return last?.Sha256 == sha ? null : new SourceVersion(sha[7..19], "https://query.wikidata.org/sparql", Sha256: sha);
    }

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(workDirectory.FullName, "wikidata-adm1-labels.csv");
        var bytes = await DownloadAsync(http, cancellationToken);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return FromFiles(Id, version, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)), path);
    }

    private static async Task<byte[]> DownloadAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://query.wikidata.org/sparql?query=" + Uri.EscapeDataString(Query));
        request.Headers.Accept.ParseAdd("text/csv");
        request.Headers.UserAgent.ParseAdd("MintPlayer.Spark.ReferenceData/11 (https://github.com/MintPlayer/MintPlayer.Spark)");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }
}

/// <summary>
/// Unicode CLDR (<c>global-cldr</c>): time-zone exemplar cities per locale (§4.13, D36). Pinned to a cldr-json release;
/// the change check is the latest release tag.
/// </summary>
internal sealed class CldrSource : EmbeddedFixtureSource
{
    public const string SourceId = "global-cldr";
    public static readonly string[] Locales = ["de", "en", "fr", "nl"];
    public override string Id => SourceId;
    public override SourceLicence Licence => GlobalLicences.Cldr;
    public override IReadOnlyList<string> DownloadHosts => ["api.github.com", "raw.githubusercontent.com"];
    protected override IReadOnlyList<string> FixtureNames => ["cldr-exemplar-cities.cut.json"];

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/unicode-org/cldr-json/releases/latest");
        request.Headers.UserAgent.ParseAdd("MintPlayer.Spark.ReferenceData");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken) ?? throw new InvalidDataException("CLDR: no release.");
        return last?.Version == release.tag_name ? null
            : new SourceVersion(release.tag_name, $"https://raw.githubusercontent.com/unicode-org/cldr-json/{release.tag_name}/cldr-json/cldr-dates-full/main");
    }

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        // The full per-locale files, rewritten into the fixture's shape (zone → locale → city) by CldrExemplarCities.
        var zones = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var locale in Locales)
        {
            var path = Path.Combine(workDirectory.FullName, $"tz-{locale}.json");
            await RefDownloads.DownloadAsync(http, $"{version.Url}/{locale}/timeZoneNames.json", path, 16L * 1024 * 1024, cancellationToken);
            await using var stream = File.OpenRead(path);
            foreach (var (zone, city) in CldrExemplarCities.ReadLocaleFile(stream, locale))
            {
                if (!zones.TryGetValue(zone, out var byLocale)) zones[zone] = byLocale = new(StringComparer.Ordinal);
                byLocale[locale] = city;
            }
        }
        var output = Path.Combine(workDirectory.FullName, "cldr-exemplar-cities.json");
        await File.WriteAllTextAsync(output, CldrExemplarCities.Write(version.Version, version.Url, zones), cancellationToken);
        return FromFiles(Id, version, null, output);
    }

    private sealed record GitHubRelease(string tag_name);
}

/// <summary>The tz database's zone list (<c>global-tzdata</c>): every assigned zone id is validated against it (RS20).</summary>
internal sealed class TzDataSource : EmbeddedFixtureSource
{
    public const string SourceId = "global-tzdata";
    public override string Id => SourceId;
    public override SourceLicence Licence => GlobalLicences.TzData;
    public override IReadOnlyList<string> DownloadHosts => ["api.github.com", "raw.githubusercontent.com"];
    protected override IReadOnlyList<string> FixtureNames => ["zone.tab"];

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/eggert/tz/tags?per_page=1");
        request.Headers.UserAgent.ParseAdd("MintPlayer.Spark.ReferenceData");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var tags = await response.Content.ReadFromJsonAsync<List<GitHubTag>>(cancellationToken) ?? [];
        var tag = tags.FirstOrDefault()?.name ?? throw new InvalidDataException("tzdata: no tag.");
        return last?.Version == tag ? null : new SourceVersion(tag, $"https://raw.githubusercontent.com/eggert/tz/{tag}/zone.tab");
    }

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(workDirectory.FullName, "zone.tab");
        var sha = await RefDownloads.DownloadAsync(http, version.Url, path, 1024 * 1024, cancellationToken);
        return FromFiles(Id, version, sha, path);
    }

    private sealed record GitHubTag(string name);
}
