using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData.Sources.Norway;

/// <summary>Credit lines (PRD §4.4, §4.12; research §4).</summary>
internal static class NorwayLicences
{
    public static readonly SourceLicence Ssb = new(
        "Statistisk sentralbyrå (SSB) KLASS", "CC BY 4.0", "https://www.ssb.no/en/informasjon/copyright",
        "Kilde: Statistisk sentralbyrå (SSB), KLASS (https://www.ssb.no/klass). The data was modified: periods, recodes and mergers are derived from the classification versions and change tables.");

    public static readonly SourceLicence Kartverket = new(
        "Kartverket Matrikkelen – Adresse", "CC BY 4.0", "https://www.kartverket.no/api-og-data/vilkar-for-bruk",
        "© Kartverket (https://www.kartverket.no). The data was modified: streets and their postcode links are derived from the address points; house numbers are not kept.");

    public static readonly SourceLicence Bring = new(
        "Bring postnummerregister", "NLOD 2.0", "https://data.norge.no/nlod/no/2.0",
        "Postnummerregister from Bring (Posten Norge AS), under the Norwegian Licence for Open Government Data (NLOD) 2.0 as stated on data.norge.no; bring.no itself states no licence. The data was modified: localities are linked to municipalities.");
}

/// <summary>
/// SSB KLASS (<c>no-klass</c>): classification 131 (kommuner) and 104 (fylker), their versions as code lists with
/// validity, and their change tables. <b>kommunenummer is not stable</b> (counties renumbered in 2020 and 2024), so
/// units are keyed on (code, valid from) and recodes keep the place (PRD §4.4). The change check is the classifications'
/// <c>lastModified</c>.
/// </summary>
internal sealed partial class NorwayKlassSource : IReferenceDataSource
{
    public const string SourceId = "no-klass";
    public const string Api = "https://data.ssb.no/api/klass/v1/classifications/";

    /// <summary>The history the import asks KLASS for (the profile's "history known since").</summary>
    public const string RequestedFrom = "1970-01-01";

    public string Id => SourceId;
    public string Country => "no";
    public SourceLicence Licence => NorwayLicences.Ssb;
    public IReadOnlyList<string> DownloadHosts => ["data.ssb.no"];
    public string DefaultSchedule => "0 0 5 * * 1";

    /// <summary><c>klass-{classification}-{codes|changes}-{from}-{to|open}[.cut].json</c>: the requested range is in the name.</summary>
    [GeneratedRegex(@"^klass-(?<classification>\d+)-(?<kind>codes|changes)-(?<from>\d{4}-\d{2}-\d{2})-(?<to>\d{4}-\d{2}-\d{2}|open)(\.cut)?\.json$")]
    internal static partial Regex FilePattern();

    public async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var modified = new List<string>();
        foreach (var classification in new[] { "104", "131" })
        {
            var meta = await http.GetFromJsonAsync<KlassClassification>(Api + classification, cancellationToken)
                ?? throw new InvalidDataException($"KLASS {classification}: no metadata.");
            modified.Add(meta.LastModified ?? string.Empty);
        }
        var version = string.Join("+", modified);
        return last?.Version == version ? null : new SourceVersion(version, Api);
    }

    public async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var files = new List<SourceFile>();
        foreach (var classification in new[] { "104", "131" })
            foreach (var kind in new[] { "codes", "changes" })
            {
                var name = $"klass-{classification}-{kind}-{RequestedFrom}-open.json";
                var path = Path.Combine(workDirectory.FullName, name);
                // No "to": KLASS answers up to today, so the importer needs no clock (K10).
                await RefDownloads.DownloadAsync(http, $"{Api}{classification}/{kind}.json?from={RequestedFrom}", path, 64L * 1024 * 1024, cancellationToken);
                files.Add(new SourceFile(name, () => File.OpenRead(path)));
            }
        return new SourceInput { SourceId = Id, Version = version.Version, Url = Api, Files = files };
    }

    public SourceInput Fixture() => new()
    {
        SourceId = Id,
        Version = "fixture",
        Files = [.. EmbeddedSourceFiles.Under(typeof(NorwayKlassSource).Assembly, "Fixtures/").Where(f => FilePattern().IsMatch(f.Name))],
    };

    private sealed record KlassClassification([property: JsonPropertyName("lastModified")] string? LastModified);
}

/// <summary>
/// Kartverket's address files (<c>no-adresser</c>), CSV in ZIP, from the Atom feed (the API caps at 10,000 results, so
/// files are used). The national file is a week behind the municipal ones. Streets and their postcode links are
/// derived from the address points; no house number is kept (RD5). The change check is the feed entry's id, which
/// carries the file's timestamp.
/// </summary>
internal sealed partial class NorwayAddressSource : RefFileSource
{
    public const string SourceId = "no-adresser";
    public const string Feed = "https://nedlasting.geonorge.no/geonorge/ATOM-feeds/MatrikkelenAdresse_AtomFeedCSV.xml";
    public const string National = "https://nedlasting.geonorge.no/geonorge/Basisdata/MatrikkelenAdresse/CSV/Basisdata_0000_Norge_25833_MatrikkelenAdresse_CSV.zip";

    public override string Id => SourceId;
    public override string Country => "no";
    public override SourceLicence Licence => NorwayLicences.Kartverket;
    public override IReadOnlyList<string> DownloadHosts => ["nedlasting.geonorge.no"];
    public override string DefaultSchedule => "0 0 5 * * 0";
    protected override string DownloadUrl => National;
    protected override Regex? EntryPattern => AddressFile();
    protected override IReadOnlyList<string> FixtureNames =>
    [
        "Basisdata_5540_Kafjord_25833_MatrikkelenAdresse.cut.csv",
        "Basisdata_5612_Kautokeino_25833_MatrikkelenAdresse.cut.csv",
    ];

    [GeneratedRegex(@"(?i)matrikkelenAdresse.*\.csv$")]
    internal static partial Regex AddressFile();

    [GeneratedRegex(@"<id>[^<]*Basisdata_0000_Norge_25833_MatrikkelenAdresse_CSV\.zip_(?<stamp>[0-9T:\-]+)</id>")]
    private static partial Regex NationalEntry();

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var feed = await http.GetStringAsync(Feed, cancellationToken);
        var stamp = NationalEntry().Match(feed) is { Success: true } m ? m.Groups["stamp"].Value
            : throw new InvalidDataException("The Kartverket address feed lists no national CSV file.");
        return last?.Version == stamp ? null : new SourceVersion(stamp, National);
    }
}

/// <summary>
/// Bring's postcode register (<c>no-postnummer</c>): tab-separated, Windows-1252, CRLF, no header: postnummer, poststed,
/// kommunenummer, kommunenavn, category (G street addresses, P post boxes, B both, F several uses, S service codes).
/// NLOD 2.0 according to data.norge.no only (bring.no states no licence; PRD §4.4). Change check: <c>ETag</c>.
/// </summary>
internal sealed class NorwayPostcodeSource : RefFileSource
{
    public const string SourceId = "no-postnummer";

    public override string Id => SourceId;
    public override string Country => "no";
    public override SourceLicence Licence => NorwayLicences.Bring;
    public override IReadOnlyList<string> DownloadHosts => ["www.bring.no"];
    public override string DefaultSchedule => "0 30 5 * * 1";
    protected override string DownloadUrl => "https://www.bring.no/postnummerregister-ansi.txt";
    protected override long MaxBytes => 16L * 1024 * 1024;
    protected override IReadOnlyList<string> FixtureNames => ["postnummerregister-ansi.cut.txt"];
}
