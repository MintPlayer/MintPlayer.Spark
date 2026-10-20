using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData.Sources.Slovenia;

/// <summary>Credit lines (PRD §4.4, §4.12; research §4).</summary>
internal static class SloveniaLicences
{
    /// <summary>"Geodetska uprava Republike Slovenije, {register}, {year}": the year of the data.</summary>
    public static SourceLicence Gurs(string register, int dataYear) => new(
        "GURS " + register, "CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/",
        $"Geodetska uprava Republike Slovenije, {register}, {dataYear.ToString(CultureInfo.InvariantCulture)} (https://www.e-prostor.gov.si/). " +
        "The data was modified: units, streets and postal areas are linked through the address register; house numbers are not kept. " +
        "GURS may change structures and terms at any time.");
}

/// <summary>One layer read from a GURS WFS (GeoServer, WFS 2.0.0, CSV output, no geometry).</summary>
/// <param name="Endpoint">The WFS endpoint.</param>
/// <param name="TypeName">The feature type: <c>SI.GURS.RPE:OBCINE</c>.</param>
/// <param name="Properties">The attributes requested; the geometry never is.</param>
/// <param name="SortKey">A unique attribute for stable paging.</param>
internal sealed record GursLayer(string Endpoint, string TypeName, string Properties, string SortKey)
{
    /// <summary>The file a layer is saved as (and its fixture's stem): <c>OBCINE.csv</c>.</summary>
    public string FileName => TypeName[(TypeName.IndexOf(':') + 1)..] + ".csv";

    public string Url(string extra) =>
        $"{Endpoint}?service=WFS&version=2.0.0&request=GetFeature&outputFormat=csv&typeNames={Uri.EscapeDataString(TypeName)}&{extra}";
}

/// <summary>
/// A GURS register read through its free WFS: there is no bulk download URL (bulk only through the JGP/eGP apps), so the
/// layers are paged as CSV without geometry. The change check asks each layer for its newest <c>DATUM_SYS</c> (one row,
/// sorted descending): the version is the newest timestamp of all layers, so any edit in the register is seen.
/// </summary>
internal abstract class GursWfsSource : IReferenceDataSource
{
    public const string Rpe = "https://ipi.eprostor.gov.si/wfs-si-gurs-rpe/wfs";
    public const string Rn = "https://ipi.eprostor.gov.si/wfs-si-gurs-rn/wfs";
    private const int PageSize = 50_000;

    public abstract string Id { get; }
    public string Country => "si";
    public SourceLicence Licence => SloveniaLicences.Gurs(Register, DataYear);
    public IReadOnlyList<string> DownloadHosts => ["ipi.eprostor.gov.si"];
    public string DefaultSchedule => "0 30 5 * * 0";

    protected abstract string Register { get; }
    protected abstract IReadOnlyList<GursLayer> Layers { get; }

    /// <summary>The year of the data: the newest <c>DATUM_SYS</c> seen; the fixture was retrieved on 2026-10-19.</summary>
    protected int DataYear { get; private set; } = 2026;

    public async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var newest = DateTimeOffset.MinValue;
        foreach (var layer in Layers)
        {
            var csv = await http.GetStringAsync(layer.Url("propertyName=DATUM_SYS&count=1&sortBy=DATUM_SYS%20DESC"), cancellationToken);
            var line = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).FirstOrDefault();
            if (line?.Split(',').LastOrDefault() is { } stamp
                && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && at > newest)
                newest = at;
        }
        if (newest == DateTimeOffset.MinValue)
            throw new InvalidDataException($"{Id}: no layer answered a DATUM_SYS.");
        DataYear = newest.Year;
        var version = newest.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return last?.Version == version ? null : new SourceVersion(version, Layers[0].Endpoint);
    }

    public async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var files = new List<SourceFile>();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var layer in Layers)
        {
            var path = Path.Combine(workDirectory.FullName, layer.FileName);
            await using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" })
            {
                for (var start = 0; ; start += PageSize)
                {
                    var page = await http.GetStringAsync(layer.Url(
                        $"propertyName={layer.Properties}&count={PageSize}&startIndex={start}&sortBy={layer.SortKey}"), cancellationToken);
                    var lines = page.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
                    if (lines.Count == 0) break;
                    // The first page keeps its header; later pages repeat it.
                    foreach (var line in start == 0 ? lines : lines.Skip(1))
                    {
                        await writer.WriteLineAsync(line);
                        sha.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
                    }
                    if (lines.Count - 1 < PageSize) break;
                }
            }
            files.Add(new SourceFile(layer.FileName, () => File.OpenRead(path)));
        }
        if (DateTime.TryParseExact(version.Version, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp))
            DataYear = stamp.Year;
        return new SourceInput
        {
            SourceId = Id, Version = version.Version, Url = version.Url, Sha256 = "sha256:" + Convert.ToHexStringLower(sha.GetHashAndReset()), Files = files,
            // The credit line carries the year of this version's data.
            Attribution = SloveniaLicences.Gurs(Register, DataYear).Attribution,
        };
    }

    public SourceInput Fixture() => new()
    {
        SourceId = Id,
        Version = "fixture",
        Attribution = SloveniaLicences.Gurs(Register, 2026).Attribution,
        Files = [.. EmbeddedSourceFiles.Under(GetType().Assembly, "Fixtures/")
            .Where(f => Layers.Any(l => f.Name == l.FileName.Replace(".csv", ".cut.csv", StringComparison.Ordinal)))],
    };
}

/// <summary>
/// The Register of Spatial Units (<c>si-rpe</c>): statistical regions, municipalities (občine), settlements (naselja),
/// streets (ulice) and postal areas (poštni okoliši), each with its EID (persistent since the March 2024 renewal), its
/// code and its Slovenian name plus the Italian or Hungarian name in the bilingual areas (<c>NAZIV_DJ</c>).
/// </summary>
internal sealed class SloveniaUnitsSource : GursWfsSource
{
    public const string SourceId = "si-rpe";
    public override string Id => SourceId;
    protected override string Register => "Register prostorskih enot";

    protected override IReadOnlyList<GursLayer> Layers { get; } =
    [
        new(Rpe, "SI.GURS.RPE:STATISTICNE_REGIJE", "EID_STATISTICNA_REGIJA,SIFRA,NAZIV,DATUM_SYS,NAZIV_DJ,EID_KOHEZIJSKA_REGIJA", "EID_STATISTICNA_REGIJA"),
        new(Rpe, "SI.GURS.RPE:OBCINE", "EID_OBCINA,SIFRA,NAZIV,OZNAKA_MESTNE_OBCINE,DATUM_SYS,NAZIV_DJ", "EID_OBCINA"),
        new(Rpe, "SI.GURS.RPE:NASELJA", "EID_NASELJE,SIFRA,NAZIV,NAZIV_KRATKO,EID_OBCINA,EID_UPRAVNA_ENOTA,ULICNI_SISTEM,DATUM_SYS,NAZIV_DJ,SESTAVLJENA_SIFRA", "EID_NASELJE"),
        new(Rpe, "SI.GURS.RPE:ULICE", "EID_ULICA,SIFRA,NAZIV,NAZIV_KRATKO,EID_OBCINA,EID_NASELJE,DATUM_SYS,NAZIV_DJ,SESTAVLJENA_SIFRA", "EID_ULICA"),
        new(Rpe, "SI.GURS.RPE:POSTNI_OKOLISI", "EID_POSTNI_OKOLIS,SIFRA,NAZIV,DATUM_SYS,NAZIV_DJ", "EID_POSTNI_OKOLIS"),
    ];
}

/// <summary>
/// The Register of Addresses (<c>si-rn</c>): read only for the links it carries (street ↔ postal area, settlement ↔
/// postal area, municipality ↔ statistical region); house numbers are neither requested nor kept (RD5).
/// </summary>
internal sealed class SloveniaAddressesSource : GursWfsSource
{
    public const string SourceId = "si-rn";
    public override string Id => SourceId;
    protected override string Register => "Register naslovov";

    protected override IReadOnlyList<GursLayer> Layers { get; } =
    [
        new(Rn, "SI.GURS.RN:REGISTER_NASLOVOV",
            "EID_NASLOV,OBCINA_SIFRA,OBCINA_NAZIV,OBCINA_NAZIV_DJ,NASELJE_SIFRA,NASELJE_NAZIV,NASELJE_NAZIV_DJ,ULICA_SIFRA,ULICA_NAZIV,ULICA_NAZIV_DJ," +
            "POSTNI_OKOLIS_SIFRA,POSTNI_OKOLIS_NAZIV,POSTNI_OKOLIS_NAZIV_DJ,EID_OBCINA,EID_NASELJE,EID_ULICA,EID_POSTNI_OKOLIS,EID_STATISTICNA_REGIJA,DATUM_SYS",
            "EID_NASLOV"),
    ];
}

internal static partial class SloveniaFiles
{
    [GeneratedRegex(@"^(?<layer>[A-Z_]+?)(\.cut)?\.csv$")]
    public static partial Regex Layer();
}
