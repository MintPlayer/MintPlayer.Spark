using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData.Sources.Switzerland;

/// <summary>Credit lines (PRD §4.4, §4.12; research §4).</summary>
internal static class SwissLicences
{
    /// <summary>
    /// swisstopo requires attribution "in every case": every API response that carries CH data and the service's own
    /// metadata (<c>/api/v1/sources</c>) show this line. The STAC metadata says <c>"license": "proprietary"</c>; the OGD
    /// terms of swisstopo govern and allow commercial use (noted, PRD §4.4).
    /// </summary>
    public static readonly SourceLicence Swisstopo = new(
        "©swisstopo", "OGD swisstopo (free use, attribution required)", "https://www.swisstopo.admin.ch/en/terms-of-use-free-geodata-and-geoservices",
        "©swisstopo — official street directory (amtliches Strassenverzeichnis). The data was modified: streets are linked to communes and postal localities.");

    /// <summary>The BFS terms are not verified (PRD §4.4); opendata.swiss's "open use, source required" is followed.</summary>
    public static readonly SourceLicence Bfs = new(
        "Federal Statistical Office (FSO/BFS)", "OGD (open use, source required)", "https://opendata.swiss/en/terms-of-use",
        "Source: Federal Statistical Office (FSO), Swiss official commune register (Amtliches Gemeindeverzeichnis der Schweiz). The data was modified: periods and lineage are derived from its mutations.");
}

/// <summary>
/// The BFS commune register (<c>ch-bfs-communes</c>): the snapshot of cantons, districts and communes on the day of the
/// fetch, and every mutation since 1960 (BFS numbers with their historicization numbers). The service sends no
/// validator, so the change check is the SHA-256 of the mutation list (RD1).
/// </summary>
internal sealed class SwitzerlandCommunesSource : IReferenceDataSource
{
    public const string SourceId = "ch-bfs-communes";
    private const string Api = "https://www.agvchapp.bfs.admin.ch/api/communes/";

    public string Id => SourceId;
    public string Country => "ch";
    public SourceLicence Licence => SwissLicences.Bfs;
    public IReadOnlyList<string> DownloadHosts => ["www.agvchapp.bfs.admin.ch"];
    public string DefaultSchedule => "0 0 5 * * 1";

    private static string Day(DateTimeOffset day) => day.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

    private static string MutationsUrl()
    {
        var now = TimeProvider.System.GetUtcNow();
        // Mutations are published ahead of their effective date (1 January), so the window reaches into next year.
        return $"{Api}mutations?startPeriod=01-01-1960&endPeriod=01-01-{now.Year + 2}";
    }

    public async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var bytes = await http.GetByteArrayAsync(MutationsUrl(), cancellationToken);
        var sha = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        return last?.Sha256 == sha ? null : new SourceVersion(sha[7..19], MutationsUrl(), Sha256: sha);
    }

    public async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var day = TimeProvider.System.GetUtcNow();
        var snapshot = Path.Combine(workDirectory.FullName, $"bfs-communes-snapshot-{day:yyyy-MM-dd}.csv");
        var mutations = Path.Combine(workDirectory.FullName, "bfs-communes-mutations.csv");
        await RefDownloads.DownloadAsync(http, $"{Api}snapshot?date={Day(day)}", snapshot, 64L * 1024 * 1024, cancellationToken);
        var sha = await RefDownloads.DownloadAsync(http, MutationsUrl(), mutations, 64L * 1024 * 1024, cancellationToken);
        return new SourceInput
        {
            SourceId = Id,
            Version = version.Version,
            Url = version.Url,
            Sha256 = sha,
            Files = [new SourceFile(Path.GetFileName(snapshot), () => File.OpenRead(snapshot)), new SourceFile(Path.GetFileName(mutations), () => File.OpenRead(mutations))],
        };
    }

    public SourceInput Fixture() => new()
    {
        SourceId = Id,
        Version = "fixture",
        Files = [.. EmbeddedSourceFiles.Under(typeof(SwitzerlandCommunesSource).Assembly, "Fixtures/").Where(f => f.Name.StartsWith("bfs-communes-", StringComparison.Ordinal))],
    };
}

/// <summary>
/// swisstopo's official street directory (<c>ch-swisstopo-streets</c>, daily): ESID streets with their commune (BFS
/// number) and postal localities (PLZ + Ortschaft). The change check is the asset's <c>file:checksum</c> in the STAC
/// item; the street directory carries the postcodes, so the 143 MB building-address file is not needed (RD5).
/// </summary>
internal sealed partial class SwitzerlandStreetsSource : RefFileSource
{
    public const string SourceId = "ch-swisstopo-streets";
    private const string Item = "https://data.geo.admin.ch/api/stac/v1/collections/ch.swisstopo.amtliches-strassenverzeichnis/items/amtliches-strassenverzeichnis_ch";
    private const string Asset = "amtliches-strassenverzeichnis_ch_2056.csv.zip";

    public override string Id => SourceId;
    public override string Country => "ch";
    public override SourceLicence Licence => SwissLicences.Swisstopo;
    public override IReadOnlyList<string> DownloadHosts => ["data.geo.admin.ch"];
    public override string DefaultSchedule => "0 30 5 * * *";
    protected override string DownloadUrl => "https://data.geo.admin.ch/ch.swisstopo.amtliches-strassenverzeichnis/amtliches-strassenverzeichnis_ch/" + Asset;
    protected override Regex? EntryPattern => StreetFile();
    protected override long MaxBytes => 512L * 1024 * 1024;
    protected override IReadOnlyList<string> FixtureNames => ["amtliches-strassenverzeichnis_ch_2056.cut.csv"];

    [GeneratedRegex(@"^amtliches-strassenverzeichnis_ch_\d+(\.cut)?\.csv$")]
    internal static partial Regex StreetFile();

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var item = JsonNode.Parse(await http.GetStringAsync(Item, cancellationToken));
        var checksum = item?["assets"]?[Asset]?["file:checksum"]?.GetValue<string>();
        if (checksum is null)
            return await base.CheckAsync(last, http, cancellationToken);
        return last?.Version == checksum ? null : new SourceVersion(checksum, DownloadUrl);
    }
}
