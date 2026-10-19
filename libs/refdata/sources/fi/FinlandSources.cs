using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData.Sources.Finland;

/// <summary>Credit lines (PRD §4.4, §4.12; research §4).</summary>
internal static class FinlandLicences
{
    public static readonly SourceLicence StatisticsFinland = new(
        "Statistics Finland classifications", "CC BY 4.0", "https://stat.fi/en/about-us/data-licenses",
        "Source: Statistics Finland, classifications of municipalities and regions (https://stat.fi). The data was modified: periods and parents are derived from the yearly versions.");

    /// <summary>⚠️ The wording follows SYKE's open-data terms (CC BY 4.0) as summarized in research §2; verify the exact credit before shipping.</summary>
    public static readonly SourceLicence Ryhti = new(
        "SYKE Ryhti building and address register", "CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/",
        "Source: Finnish Environment Institute (Syke), Ryhti building and address register (https://paikkatiedot.ymparisto.fi). The data was modified: streets and postcodes are derived from the address points; house numbers are not kept.");
}

/// <summary>
/// Statistics Finland's classification service (<c>fi-kunta</c>): the yearly versions of the municipality list
/// (<c>kunta_1_{yyyyMMdd}</c>, since 1970) and the region list (<c>maakunta_1_…</c>, since 1992), in Finnish and Swedish,
/// with the municipality → region and municipality → language-status correspondence tables. The change check is the
/// newest municipality version's date.
/// </summary>
internal sealed partial class FinlandClassificationSource : IReferenceDataSource
{
    public const string SourceId = "fi-kunta";
    public const string Api = "https://data.stat.fi/api/classifications/v2/";

    public string Id => SourceId;
    public string Country => "fi";
    public SourceLicence Licence => FinlandLicences.StatisticsFinland;
    public IReadOnlyList<string> DownloadHosts => ["data.stat.fi"];
    public string DefaultSchedule => "0 0 5 2 1 *";

    /// <summary><c>kunta_1_20210101.fi.json</c>, <c>maakunta_1_20210101.sv.cut.json</c>.</summary>
    [GeneratedRegex(@"^(?<classification>kunta|maakunta)_1_(?<date>\d{8})\.(?<lang>fi|sv)(\.cut)?\.json$")]
    internal static partial Regex ItemsFile();

    /// <summary><c>kunta_1_20210101-maakunta_1_20210101.json</c>, <c>kunta_1_20210101-kielisuhde_1_20210101.cut.json</c>.</summary>
    [GeneratedRegex(@"^kunta_1_(?<date>\d{8})-(?<target>maakunta|kielisuhde)_1_\d{8}(\.cut)?\.json$")]
    internal static partial Regex MapFile();

    [GeneratedRegex(@"/(?<name>(kunta|maakunta)_1_(?<date>\d{8}))$")]
    private static partial Regex ClassificationUrl();

    [GeneratedRegex(@"/(?<name>kunta_1_\d{8}%23(maakunta|kielisuhde)_1_\d{8})$")]
    private static partial Regex CorrespondenceUrl();

    public async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var latest = (await ClassificationsAsync(http, cancellationToken)).Where(c => c.Name.StartsWith("kunta_", StringComparison.Ordinal)).Max(c => c.Date)
            ?? throw new InvalidDataException("Statistics Finland lists no municipality classification.");
        return last?.Version == latest ? null : new SourceVersion(latest, Api);
    }

    public async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var files = new List<SourceFile>();
        async Task Get(string url, string name)
        {
            var path = Path.Combine(workDirectory.FullName, name);
            await RefDownloads.DownloadAsync(http, url, path, 32L * 1024 * 1024, cancellationToken);
            files.Add(new SourceFile(name, () => File.OpenRead(path)));
        }

        foreach (var (name, date) in (await ClassificationsAsync(http, cancellationToken)).Where(c => string.CompareOrdinal(c.Date, "19700101") >= 0))
            foreach (var lang in new[] { "fi", "sv" })
                await Get($"{Api}classifications/{name}/classificationItems?content=data&meta=max&lang={lang}", $"{name}.{lang}.json");

        using var request = await http.GetAsync($"{Api}correspondenceTables?searchText=kunta_1_", cancellationToken);
        request.EnsureSuccessStatusCode();
        foreach (var url in await request.Content.ReadFromJsonAsync<List<string>>(cancellationToken) ?? [])
            if (CorrespondenceUrl().Match(url) is { Success: true } m)
                await Get($"{Api}correspondenceTables/{m.Groups["name"].Value}/maps?content=data&meta=max&lang=fi", m.Groups["name"].Value.Replace("%23", "-", StringComparison.Ordinal) + ".json");
        return new SourceInput { SourceId = Id, Version = version.Version, Url = Api, Files = files };
    }

    public SourceInput Fixture() => new()
    {
        SourceId = Id,
        Version = "fixture",
        Files = [.. EmbeddedSourceFiles.Under(typeof(FinlandClassificationSource).Assembly, "Fixtures/").Where(f => ItemsFile().IsMatch(f.Name) || MapFile().IsMatch(f.Name))],
    };

    private async Task<List<(string Name, string Date)>> ClassificationsAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var result = new List<(string, string)>();
        foreach (var search in new[] { "kunta_1_", "maakunta_1_" })
        {
            var urls = await http.GetFromJsonAsync<List<string>>($"{Api}classifications?searchText={search}", cancellationToken) ?? [];
            foreach (var url in urls)
                if (ClassificationUrl().Match(url) is { Success: true } m)
                    result.Add((m.Groups["name"].Value, m.Groups["date"].Value));
        }
        return [.. result.Distinct().OrderBy(r => r.Item1, StringComparer.Ordinal)];
    }
}

/// <summary>
/// SYKE's Ryhti building and address register (<c>fi-ryhti</c>), the <c>open_address</c> collection of its OGC API
/// Features service, paged to GeoJSON files. Streets, their names in Finnish and Swedish and their postcodes come from
/// the address points; no house number is kept (RD5). Postcodes come from here, not from Posti: Posti's files may be
/// passed on only with its service description and terms (PRD §4.4) unless Posti confirms republication in writing.
/// The change check is the newest <c>modified_timestamp_utc</c> with the feature count.
/// </summary>
internal sealed class FinlandRyhtiSource : IReferenceDataSource
{
    public const string SourceId = "fi-ryhti";
    public const string Items = "https://paikkatiedot.ymparisto.fi/geoserver/ryhti_building/ogc/features/v1/collections/open_address/items";
    private const int PageSize = 10000;

    public string Id => SourceId;
    public string Country => "fi";
    public SourceLicence Licence => FinlandLicences.Ryhti;
    public IReadOnlyList<string> DownloadHosts => ["paikkatiedot.ymparisto.fi"];
    public string DefaultSchedule => "0 0 4 * * 0";

    public async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var page = await http.GetFromJsonAsync<JsonObject>($"{Items}?f=json&limit=1&sortby=-modified_timestamp_utc", cancellationToken)
            ?? throw new InvalidDataException("Ryhti answered no page.");
        var modified = page["features"]?.AsArray().FirstOrDefault()?["properties"]?["modified_timestamp_utc"]?.GetValue<string>() ?? "none";
        var version = $"{modified}/{page["numberMatched"]}";
        return last?.Version == version ? null : new SourceVersion(version, Items);
    }

    public async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var files = new List<SourceFile>();
        var url = $"{Items}?f=json&limit={PageSize}&sortby=address_key";
        for (var n = 1; url is not null; n++)
        {
            var name = $"ryhti-open_address-{n:D5}.json";
            var path = Path.Combine(workDirectory.FullName, name);
            await RefDownloads.DownloadAsync(http, url, path, 256L * 1024 * 1024, cancellationToken);
            files.Add(new SourceFile(name, () => File.OpenRead(path)));
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            url = document.RootElement.TryGetProperty("links", out var links)
                ? links.EnumerateArray().Where(l => l.TryGetProperty("rel", out var rel) && rel.GetString() == "next")
                    .Select(l => l.GetProperty("href").GetString()).FirstOrDefault()
                : null;
            if (url is not null && !url.StartsWith(Items, StringComparison.Ordinal))
                throw new InvalidDataException("Ryhti's next link leaves the collection.");
        }
        return new SourceInput { SourceId = Id, Version = version.Version, Url = Items, Files = files };
    }

    public SourceInput Fixture() => new()
    {
        SourceId = Id,
        Version = "fixture",
        Files = [.. EmbeddedSourceFiles.Under(typeof(FinlandRyhtiSource).Assembly, "Fixtures/").Where(f => f.Name.StartsWith("ryhti-open_address", StringComparison.Ordinal))],
    };
}
