using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData.Sources.UnitedStates;

/// <summary>
/// Credit lines (PRD §4.4, §4.12): the U.S. Census Bureau is credited as the source, never as an endorser, and the
/// trademark "TIGER" never appears in a product name. ZIP Codes are flagged as not authoritative in every response.
/// </summary>
internal static class UnitedStatesLicences
{
    public static readonly SourceLicence Census = new(
        "U.S. Census Bureau", "Public domain (U.S. Government work)", "https://www.census.gov/about/policies/open-gov/open-data.html",
        "Source: U.S. Census Bureau, geographic codes and county changes (https://www.census.gov/geographies/reference-files.html). " +
        "The data was modified: county changes are recorded as dated lineage. Not endorsed by the U.S. Census Bureau.");

    public static readonly SourceLicence AddressRanges = new(
        "U.S. Census Bureau address-range features", "Public domain (U.S. Government work)", "https://www.census.gov/about/policies/open-gov/open-data.html",
        "Source: U.S. Census Bureau, address-range feature files (https://www.census.gov/geographies/mapping-files.html). " +
        "ZIP Codes are derived from Census address ranges (potential ranges, maintained by the Census Bureau) and are NOT authoritative USPS data. " +
        "The data was modified: street names are linked to counties and ZIP Codes; house-number ranges are not kept. Not endorsed by the U.S. Census Bureau.");
}

/// <summary>
/// The Census Bureau's state and county code lists (<c>us-codes</c>): <c>national_state2020.txt</c> and
/// <c>national_county2020.txt</c> (pipe-separated, CRLF), the current GEOIDs with names, GNIS codes and functional status.
/// Change check: <c>ETag</c>/<c>Last-Modified</c> of the county file.
/// </summary>
internal sealed partial class UnitedStatesCodesSource : RefFileSource
{
    public const string SourceId = "us-codes";
    private const string Base = "https://www2.census.gov/geo/docs/reference/codes2020/";

    public override string Id => SourceId;
    public override string Country => "us";
    public override SourceLicence Licence => UnitedStatesLicences.Census;
    public override IReadOnlyList<string> DownloadHosts => ["www2.census.gov"];
    public override string DefaultSchedule => "0 0 6 1 * *";
    protected override string DownloadUrl => Base + "national_county2020.txt";
    protected override IReadOnlyList<string> FixtureNames => ["national_state2020.cut.txt", "national_county2020.cut.txt"];

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var files = new List<SourceFile>();
        string? sha = null;
        foreach (var name in new[] { "national_state2020.txt", "national_county2020.txt" })
        {
            var path = Path.Combine(workDirectory.FullName, name);
            sha = await RefDownloads.DownloadAsync(http, Base + name, path, 16L * 1024 * 1024, cancellationToken);
            files.Add(new SourceFile(name, () => File.OpenRead(path)));
        }
        return new SourceInput { SourceId = Id, Version = version.Version, Url = DownloadUrl, Sha256 = sha, Files = files };
    }

    [GeneratedRegex(@"^national_state\d{4}(\.cut)?\.txt$")]
    internal static partial Regex StateFile();

    [GeneratedRegex(@"^national_county\d{4}(\.cut)?\.txt$")]
    internal static partial Regex CountyFile();
}

/// <summary>
/// The Census Bureau's county changes since 1970 (<c>us-county-changes</c>): one page per decade, prose without a
/// validity model. The fetch reduces each page to its text (scripts and markup removed, entities decoded, whitespace
/// collapsed) from "New Counties or County Equivalent Entities" to "Page Last Revised"; the mapper reads the entries
/// ("Name, State (SS-CCC): … effective Month D, YYYY"). No validator is sent, so the change check hashes that text (RD1).
/// </summary>
internal sealed partial class UnitedStatesCountyChangesSource : IReferenceDataSource
{
    public const string SourceId = "us-county-changes";
    private const string Page = "https://www.census.gov/programs-surveys/geography/technical-documentation/county-changes";
    public static readonly string[] Decades = ["1970", "1980", "1990", "2000", "2010", "2020"];

    public string Id => SourceId;
    public string Country => "us";
    public SourceLicence Licence => UnitedStatesLicences.Census;
    public IReadOnlyList<string> DownloadHosts => ["www.census.gov"];
    public string DefaultSchedule => "0 0 6 1 * *";

    public async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var texts = await TextsAsync(http, cancellationToken);
        var sha = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(texts.Select(t => t.Text)))));
        return last?.Sha256 == sha ? null : new SourceVersion(sha[7..19], Page + ".html", Sha256: sha);
    }

    public async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var files = new List<SourceFile>();
        foreach (var (decade, text) in await TextsAsync(http, cancellationToken))
        {
            var path = Path.Combine(workDirectory.FullName, $"county-changes.{decade}.txt");
            await File.WriteAllTextAsync(path, text + "\n", new UTF8Encoding(false), cancellationToken);
            files.Add(new SourceFile(Path.GetFileName(path), () => File.OpenRead(path)));
        }
        return new SourceInput { SourceId = Id, Version = version.Version, Url = version.Url, Sha256 = version.Sha256, Files = files };
    }

    public SourceInput Fixture() => new()
    {
        SourceId = Id,
        Version = "fixture",
        Files = [.. EmbeddedSourceFiles.Under(typeof(UnitedStatesCountyChangesSource).Assembly, "Fixtures/").Where(f => ChangesFile().IsMatch(f.Name))],
    };

    private static async Task<List<(string Decade, string Text)>> TextsAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var texts = new List<(string, string)>();
        foreach (var decade in Decades)
        {
            // The 2020s are the landing page; earlier decades have their own.
            var url = decade == "2020" ? Page + ".html" : $"{Page}.{decade}.html";
            using var response = await http.GetAsync(url, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (html.Length > 8 * 1024 * 1024) throw new InvalidDataException($"{url} is larger than 8 MB.");
            texts.Add((decade, ExtractText(html)));
        }
        return texts;
    }

    /// <summary>The page's change entries as one line of text (also how the fixtures were cut).</summary>
    internal static string ExtractText(string html)
    {
        var text = ScriptOrStyle().Replace(html, string.Empty);
        text = WebUtility.HtmlDecode(Tag().Replace(text, " "));
        text = Space().Replace(text, " ");
        var start = text.IndexOf("New Counties or County Equivalent Entities", StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        var end = text.IndexOf("Page Last Revised", start, StringComparison.Ordinal);
        return text[start..(end < 0 ? text.Length : end)].Trim();
    }

    [GeneratedRegex(@"<script.*?</script>|<style.*?</style>", RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    [GeneratedRegex(@"^county-changes\.\d{4}(\.cut)?\.txt$")]
    internal static partial Regex ChangesFile();
}

/// <summary>
/// The Census Bureau's address-range feature files (<c>us-addrfeat</c>): one shapefile zip per county
/// (<c>tl_{year}_{GEOID}_addrfeat.zip</c>), of which only the attribute table (<c>.dbf</c>, UTF-8 per its <c>.cpg</c>) is
/// read: <c>LINEARID</c>, <c>FULLNAME</c> and the ZIP Code per side (<c>ZIPL</c>/<c>ZIPR</c>). House-number ranges are
/// not kept (RD5). The change check is the newest yearly release directory that exists (2026 released 2026-10-14).
/// </summary>
internal sealed partial class UnitedStatesAddressRangesSource : IReferenceDataSource
{
    public const string SourceId = "us-addrfeat";

    public string Id => SourceId;
    public string Country => "us";
    public SourceLicence Licence => UnitedStatesLicences.AddressRanges;
    public IReadOnlyList<string> DownloadHosts => ["www2.census.gov"];
    public string DefaultSchedule => "0 0 6 15 * *";

    public async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var year = TimeProvider.System.GetUtcNow().Year;
        for (var candidate = year; candidate >= year - 2; candidate--)
        {
            var url = $"https://www2.census.gov/geo/tiger/TIGER{candidate}/ADDRFEAT/";
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) continue;
            var version = candidate.ToString(CultureInfo.InvariantCulture);
            return last?.Version == version ? null : new SourceVersion(version, url);
        }
        throw new InvalidDataException($"{Id}: no yearly release directory in the last three years.");
    }

    public async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        // The county list names every file to fetch (one zip per county, ≈3,200).
        var counties = new List<string>();
        using (var stream = await http.GetStreamAsync("https://www2.census.gov/geo/docs/reference/codes2020/national_county2020.txt", cancellationToken))
            foreach (var row in RefCsv.ReadRecords(stream, '|', quotes: false))
                counties.Add(row["STATEFP"] + row["COUNTYFP"]);

        var files = new List<SourceFile>();
        foreach (var geoid in counties)
        {
            var name = $"tl_{version.Version}_{geoid}_addrfeat";
            var zip = Path.Combine(workDirectory.FullName, name + ".zip");
            try
            {
                await RefDownloads.DownloadAsync(http, $"{version.Url}{name}.zip", zip, 512L * 1024 * 1024, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                continue; // A county without address ranges.
            }
            files.AddRange(RefZip.Extract(zip, DbfFile(), workDirectory, 1024L * 1024 * 1024));
            File.Delete(zip);
        }
        return new SourceInput { SourceId = Id, Version = version.Version, Url = version.Url, Files = files };
    }

    public SourceInput Fixture() => new()
    {
        SourceId = Id,
        Version = "fixture",
        Files = [.. EmbeddedSourceFiles.Under(typeof(UnitedStatesAddressRangesSource).Assembly, "Fixtures/").Where(f => DbfFile().IsMatch(f.Name))],
    };

    [GeneratedRegex(@"^tl_(?<year>\d{4})_(?<geoid>\d{5})_addrfeat\.dbf$")]
    internal static partial Regex DbfFile();
}
