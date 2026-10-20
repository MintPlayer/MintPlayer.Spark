using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MintPlayer.Spark.ReferenceData.Sources.Netherlands;

/// <summary>Credit lines (PRD §4.4, §4.12).</summary>
internal static class NetherlandsLicences
{
    public static readonly SourceLicence Bag = new(
        "Kadaster Basisregistratie Adressen en Gebouwen (BAG)", "CC0 1.0", "https://creativecommons.org/publicdomain/zero/1.0/",
        "Bron: Kadaster, Basisregistratie Adressen en Gebouwen (LVBAG extract via PDOK). The PDOK feed labels it CC0, Kadaster's product page the Public Domain Mark; credited although neither requires it.",
        Modified: true);

    public static readonly SourceLicence Cbs = new(
        "CBS", "CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/",
        "Bron: CBS (Centraal Bureau voor de Statistiek), CC BY 4.0. The data was modified: herindelingen are turned into dated periods and lineage.");
}

/// <summary>
/// The LVBAG extract (<c>nl-bag</c>): the free monthly full extract (made on the 8th) found through the PDOK Atom feed.
/// The daily and monthly mutation files are paid (€14.50/month + €219 setup; daily €219/month) and unused, so the schedule
/// is monthly. The change check is the feed entry's <c>updated</c> date. The extract is a ZIP of ZIPs (3.64 GB); only the
/// woonplaatsen, openbare ruimten, nummeraanduidingen and Kadaster's gemeente–woonplaats relation file are unpacked.
/// </summary>
internal sealed partial class NetherlandsBagSource : RefFileSource
{
    public const string SourceId = "nl-bag";
    public const string Feed = "https://service.pdok.nl/lv/bag/atom/bag.xml";
    public const string Extract = "https://service.pdok.nl/kadaster/bag/atom/downloads/lvbag-extract-nl.zip";

    public override string Id => SourceId;
    public override string Country => "nl";
    public override SourceLicence Licence => NetherlandsLicences.Bag;
    public override IReadOnlyList<string> DownloadHosts => ["service.pdok.nl"];
    public override string DefaultSchedule => "0 0 5 10 * *";
    protected override string DownloadUrl => Extract;
    protected override long MaxBytes => 6L * 1024 * 1024 * 1024;

    protected override IReadOnlyList<string> FixtureNames =>
    [
        "9999WPL08102026-000001.cut.xml", "9999OPR08102026-000001.cut.xml", "9999NUM08102026-000001.cut.xml", "GEM-WPL-RELATIE-08102026-000001.cut.xml",
    ];

    /// <summary>The inner ZIPs read (dated names: matched by pattern).</summary>
    [GeneratedRegex(@"^(9999(WPL|OPR|NUM)\d{8}|GEM-WPL-RELATIE-\d{8})\.zip$")]
    private static partial Regex InnerZip();

    [GeneratedRegex(@"^(9999(WPL|OPR|NUM)\d{8}|GEM-WPL-RELATIE-\d{8})-\d{6}(\.cut)?\.xml$")]
    internal static partial Regex XmlFile();

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        await using var stream = await http.GetStreamAsync(Feed, cancellationToken);
        var feed = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var entry = feed.Root?.Elements(atom + "entry")
            .FirstOrDefault(e => e.Elements(atom + "link").Any(l => ((string?)l.Attribute("href"))?.EndsWith("/lvbag-extract-nl.zip", StringComparison.Ordinal) == true))
            ?? throw new InvalidDataException($"{SourceId}: the PDOK feed has no lvbag-extract-nl.zip entry.");
        var updated = (string?)entry.Element(atom + "updated") ?? throw new InvalidDataException($"{SourceId}: the feed entry states no update date.");
        var version = updated.Length >= 10 ? updated[..10] : updated;
        return last?.Version == version ? null : new SourceVersion(version, Extract);
    }

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var outer = Path.Combine(workDirectory.FullName, "lvbag-extract-nl.zip");
        var sha = await RefDownloads.DownloadAsync(http, Extract, outer, MaxBytes, cancellationToken);
        var files = new List<SourceFile>();
        foreach (var inner in RefZip.Extract(outer, InnerZip(), workDirectory, MaxBytes))
        {
            var innerPath = Path.Combine(workDirectory.FullName, inner.Name);
            files.AddRange(RefZip.Extract(innerPath, XmlFile(), workDirectory, 2L * 1024 * 1024 * 1024));
            File.Delete(innerPath);
        }
        File.Delete(outer);
        return new SourceInput { SourceId = Id, Version = version.Version, Url = Extract, Sha256 = sha, Files = files };
    }
}

/// <summary>
/// CBS <c>Gebieden in Nederland</c> (<c>nl-cbs-gemeenten</c>): the current gemeenten with their province, from the
/// OData table of the year (<see cref="Table"/>, renamed every year by CBS: a host may override it with a newer one).
/// The change check is the table's <c>Modified</c> stamp.
/// </summary>
internal sealed class NetherlandsCbsGemeentenSource : RefFileSource
{
    public const string SourceId = "nl-cbs-gemeenten";

    /// <summary>"Gebieden in Nederland 2026". ⚠️ CBS publishes a new table id each year; update with the edition.</summary>
    public const string Table = "86247NED";

    public override string Id => SourceId;
    public override string Country => "nl";
    public override SourceLicence Licence => NetherlandsLicences.Cbs;
    public override IReadOnlyList<string> DownloadHosts => ["opendata.cbs.nl"];
    public override string DefaultSchedule => "0 30 5 1 * *";
    protected override string DownloadUrl => $"https://opendata.cbs.nl/ODataApi/odata/{Table}/TypedDataSet?$select=RegioS,Code_1,Naam_2,Code_28,Naam_29";
    protected override long MaxBytes => 64L * 1024 * 1024;
    protected override IReadOnlyList<string> FixtureNames => ["cbs-86247NED-gemeenten.cut.json"];

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var info = await http.GetStringAsync($"https://opendata.cbs.nl/ODataApi/odata/{Table}/TableInfos", cancellationToken);
        var modified = Regex.Match(info, "\"Modified\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
        if (modified.Length == 0)
            throw new InvalidDataException($"{SourceId}: TableInfos of {Table} states no Modified date.");
        var version = $"{Table}@{modified}";
        return last?.Version == version ? null : new SourceVersion(version, DownloadUrl);
    }

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(workDirectory.FullName, $"cbs-{Table}-gemeenten.json");
        var sha = await RefDownloads.DownloadAsync(http, DownloadUrl, path, MaxBytes, cancellationToken);
        return new SourceInput { SourceId = Id, Version = version.Version, Url = DownloadUrl, Sha256 = sha, Files = [new SourceFile(Path.GetFileName(path), () => File.OpenRead(path))] };
    }
}

/// <summary>
/// CBS herindelingen (<c>nl-cbs-herindelingen</c>): the per-year pages "Gemeentelijke indeling op 1 januari {year}"
/// since 1991, each with a table of ended gemeenten and the gemeente they went into. CBS sends no validator and no
/// checksum, so the change check is our SHA-256 of the tables (RD1). Only the table is kept from each page.
/// </summary>
internal sealed partial class NetherlandsHerindelingenSource : RefFileSource
{
    public const string SourceId = "nl-cbs-herindelingen";
    public const string Index = "https://www.cbs.nl/nl-nl/onze-diensten/methoden/classificaties/overig/gemeentelijke-indelingen-per-jaar";

    /// <summary>The first year the adapter reads (PRD §4.4: CBS herindelingen since 1991).</summary>
    public const int FirstYear = 1991;

    public override string Id => SourceId;
    public override string Country => "nl";
    public override SourceLicence Licence => NetherlandsLicences.Cbs;
    public override IReadOnlyList<string> DownloadHosts => ["www.cbs.nl"];
    public override string DefaultSchedule => "0 45 5 1 * *";
    protected override string DownloadUrl => Index;
    protected override IReadOnlyList<string> FixtureNames => ["cbs-herindelingen-2019.cut.html", "cbs-herindelingen-2021.cut.html"];

    [GeneratedRegex(@"https://www\.cbs\.nl/nl-nl/onze-diensten/methoden/classificaties/overig/gemeentelijke-indelingen-per-jaar/indeling-per-jaar/gemeentelijke-indeling-op-1-januari-(?<year>\d{4})")]
    private static partial Regex YearPage();

    [GeneratedRegex(@"<table\b.*?</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Table();

    [GeneratedRegex(@"^cbs-herindelingen-(?<year>\d{4})(\.cut)?\.html$")]
    internal static partial Regex File();

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var tables = await TablesAsync(http, cancellationToken);
        var sha = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(tables.Select(t => $"{t.Year}\n{t.Html}\n")))));
        return last?.Sha256 == sha ? null : new SourceVersion(sha[7..19], Index, Sha256: sha);
    }

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var files = new List<SourceFile>();
        foreach (var (year, html) in await TablesAsync(http, cancellationToken))
        {
            var path = Path.Combine(workDirectory.FullName, $"cbs-herindelingen-{year}.html");
            await System.IO.File.WriteAllTextAsync(path, html, new UTF8Encoding(false), cancellationToken);
            files.Add(new SourceFile(Path.GetFileName(path), () => System.IO.File.OpenRead(path)));
        }
        return new SourceInput { SourceId = Id, Version = version.Version, Url = Index, Sha256 = version.Sha256, Files = files };
    }

    /// <summary>The herindeling table of every year page since <see cref="FirstYear"/>, by year. ⚠️ The page markup is unverified before 2019 (RS8).</summary>
    private static async Task<List<(int Year, string Html)>> TablesAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var index = await http.GetStringAsync(Index, cancellationToken);
        var pages = YearPage().Matches(index)
            .Select(m => (Year: int.Parse(m.Groups["year"].Value, System.Globalization.CultureInfo.InvariantCulture), Url: m.Value))
            .Where(p => p.Year >= FirstYear)
            .DistinctBy(p => p.Year)
            .OrderBy(p => p.Year)
            .ToList();
        var tables = new List<(int, string)>();
        foreach (var (year, url) in pages)
        {
            var html = await http.GetStringAsync(url, cancellationToken);
            if (Table().Match(html) is { Success: true } table)
                tables.Add((year, table.Value + "\n"));
        }
        return tables;
    }
}
