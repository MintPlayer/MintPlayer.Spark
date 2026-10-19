using System.Globalization;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData.Sources.Spain;

/// <summary>Credit lines (PRD §4.4, §4.12; research §4).</summary>
internal static class SpainLicences
{
    /// <summary>INE's reuse terms ask for this sentence verbatim; INE is a source, never an endorser.</summary>
    public static readonly SourceLicence Ine = new(
        "INE", "INE terms of reuse", "https://www.ine.es/dyngs/AYU/index.htm?cid=1102",
        "Elaboración propia con datos extraídos del sitio web del INE: www.ine.es");

    /// <summary>
    /// IGN/CNIG require their formula next to the data <b>and</b> in the service's own metadata ("GetCapabilities o
    /// similar"). The formula is the source's <see cref="SourceLicence.Name"/>, so the compact attribution that every API
    /// response carries (<see cref="SourceLicence.Compact"/>) holds it too; <c>/api/v1/sources</c> lists the full line.
    /// A footer on the CNIG site says CC BY-SA: the CNIG licence page (CC BY 4.0) governs; the conflict is noted.
    /// </summary>
    public static readonly SourceLicence CartoCiudad = new(
        "Obra derivada de CartoCiudad CC-BY 4.0 scne.es", "CC BY 4.0", "https://www.scne.es/productos.html#CartoCiudad",
        "Obra derivada de CartoCiudad CC-BY 4.0 scne.es (Instituto Geográfico Nacional / CNIG). The data was modified: postcodes are linked to INE municipalities.");
}

/// <summary>
/// INE's municipality codes (<c>es-ine</c>): <c>{yy}codmun.xlsx</c> (one sheet per province, published each January,
/// names in their official form, "Coruña, A", "Donostia/San Sebastián") and the community/province table
/// <c>cod_ccaa_provincia.htm</c>. Change check: the next year's file name is probed, then the current file's
/// <c>ETag</c>/<c>Last-Modified</c>.
/// </summary>
internal sealed class SpainIneCodesSource : RefFileSource
{
    public const string SourceId = "es-ine";
    public const string Base = "https://www.ine.es/daco/daco42/codmun/";
    public const string ProvincesUrl = Base + "cod_ccaa_provincia.htm";

    /// <summary>The edition the adapter was written against (2026); later ones are found by probing.</summary>
    private const int FirstYear = 26;

    public override string Id => SourceId;
    public override string Country => "es";
    public override SourceLicence Licence => SpainLicences.Ine;
    public override IReadOnlyList<string> DownloadHosts => ["www.ine.es"];
    public override string DefaultSchedule => "0 0 5 * * 1";
    protected override string DownloadUrl => Url(FirstYear);
    protected override IReadOnlyList<string> FixtureNames => ["26codmun.cut.xlsx", "cod_ccaa_provincia.cut.htm"];

    private static string Url(int yy) => $"{Base}{yy:00}codmun.xlsx";

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var year = last?.Version is { Length: >= 2 } v && int.TryParse(v[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : FirstYear;
        // A new edition is a new file name: probe forward from the last known year.
        while (year < 99 && await ExistsAsync(http, Url(year + 1), cancellationToken))
            year++;
        var check = await RefDownloads.CheckConditionalAsync(http, Url(year), last is not null && last.Version?.StartsWith($"{year:00}", StringComparison.Ordinal) == true ? last : null, cancellationToken);
        return check is null ? null : check with { Version = $"{year:00}-{check.Version}" };
    }

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var xlsx = Path.Combine(workDirectory.FullName, Path.GetFileName(new Uri(version.Url).LocalPath));
        var sha = await RefDownloads.DownloadAsync(http, version.Url, xlsx, 64L * 1024 * 1024, cancellationToken);
        var html = Path.Combine(workDirectory.FullName, "cod_ccaa_provincia.htm");
        await RefDownloads.DownloadAsync(http, ProvincesUrl, html, 4L * 1024 * 1024, cancellationToken);
        return new SourceInput
        {
            SourceId = Id, Version = version.Version, Url = version.Url, Sha256 = sha,
            Files = [new SourceFile(Path.GetFileName(xlsx), () => File.OpenRead(xlsx)), new SourceFile("cod_ccaa_provincia.htm", () => File.OpenRead(html))],
        };
    }

    private static async Task<bool> ExistsAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}

/// <summary>A source whose edition is a dated ZIP linked from the INE Callejero page (January and July releases).</summary>
internal abstract class SpainCallejeroBase : RefFileSource
{
    public const string Page = "https://www.ine.es/dyngs/DAB/es/index.htm?cid=1390";

    public override string Country => "es";
    public override SourceLicence Licence => SpainLicences.Ine;
    public override IReadOnlyList<string> DownloadHosts => ["www.ine.es"];

    /// <summary>The releases come in January and July: weekly checks of the page are plenty.</summary>
    public override string DefaultSchedule => "0 0 5 * * 2";

    protected override string DownloadUrl => Page;

    /// <summary>The link pattern; group <c>version</c> orders the editions (ordinal).</summary>
    protected abstract Regex Link { get; }

    /// <summary>Turns a link's match into a sortable version.</summary>
    protected abstract string VersionOf(Match match);

    protected override long MaxBytes => 2L * 1024 * 1024 * 1024;

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var html = await http.GetStringAsync(Page, cancellationToken);
        var latest = Link.Matches(html).Select(m => (Version: VersionOf(m), Path: m.Value)).OrderBy(e => e.Version, StringComparer.Ordinal).LastOrDefault();
        if (latest.Path is null)
            throw new InvalidDataException($"{Id}: no file matching {Link} on {Page}.");
        if (last?.Version == latest.Version)
            return null;
        return new SourceVersion(latest.Version, new Uri(new Uri(Page), latest.Path).ToString());
    }

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var zip = Path.Combine(workDirectory.FullName, RefZip.SafeName(Path.GetFileName(new Uri(version.Url).LocalPath), "callejero.zip"));
        var sha = await RefDownloads.DownloadAsync(http, version.Url, zip, MaxBytes, cancellationToken);
        return new SourceInput { SourceId = Id, Version = version.Version, Url = version.Url, Sha256 = sha, Files = RefZip.Extract(zip, EntryPattern!, workDirectory, MaxBytes) };
    }
}

/// <summary>
/// The INE Callejero (<c>es-callejero</c>): <c>caj_esp_{MM}{yyyy}.zip</c>, fixed-width ISO-8859-1 files. Read: <c>UP</c>
/// (population entities) and <c>VIAS</c> (streets). <c>TRAM</c> (≈420 MB of street segments) is not fetched: its
/// postcodes may be Correos-derived, so Spanish postcodes come from CartoCiudad (PRD §4.4).
/// </summary>
internal sealed partial class SpainCallejeroSource : SpainCallejeroBase
{
    public const string SourceId = "es-callejero";
    public override string Id => SourceId;
    protected override Regex Link => LinkPattern();
    protected override Regex? EntryPattern => EntryRegex();
    protected override IReadOnlyList<string> FixtureNames => ["UP.D260630.G260702.cut", "VIAS.D260630.G260702.cut", "TiposVia.csv"];

    protected override string VersionOf(Match match) => match.Groups["year"].Value + match.Groups["month"].Value;

    [GeneratedRegex(@"/prodyser/callejero/caj_esp/caj_esp_(?<month>\d{2})(?<year>\d{4})\.zip")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"^(UP|VIAS)\.D\d{6}\.G\d{6}$")]
    private static partial Regex EntryRegex();

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var input = await base.FetchAsync(version, http, workDirectory, cancellationToken);
        // The street-type table sits beside the releases on the same page.
        var types = Path.Combine(workDirectory.FullName, "TiposVia.csv");
        await RefDownloads.DownloadAsync(http, "https://www.ine.es/prodyser/callejero/TiposVia.csv", types, 4L * 1024 * 1024, cancellationToken);
        return new SourceInput { SourceId = input.SourceId, Version = input.Version, Url = input.Url, Sha256 = input.Sha256, Files = [.. input.Files, new SourceFile("TiposVia.csv", () => File.OpenRead(types))] };
    }
}

/// <summary>
/// The Callejero's half-yearly variation files (<c>es-callejero-var</c>, <c>Var_Callejero_{MMyy}_{MMyy}.zip</c>): street
/// altas, bajas and modifications with their dates, which give the streets' periods.
/// </summary>
internal sealed partial class SpainCallejeroVariationsSource : SpainCallejeroBase
{
    public const string SourceId = "es-callejero-var";
    public override string Id => SourceId;
    protected override Regex Link => LinkPattern();
    protected override Regex? EntryPattern => EntryRegex();
    protected override IReadOnlyList<string> FixtureNames => ["VIAS.D260101.A260630.cut"];

    protected override string VersionOf(Match match) => match.Groups["toyear"].Value + match.Groups["tomonth"].Value;

    [GeneratedRegex(@"/prodyser/callejero/Var_Callejero_\d{4}_(?<tomonth>\d{2})(?<toyear>\d{2})\.zip")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"^VIAS\.D\d{6}\.A\d{6}$")]
    private static partial Regex EntryRegex();
}

/// <summary>
/// CartoCiudad postcodes (<c>es-cartociudad</c>), PRD §4.4: the attribute table (<c>.dbf</c>) of CartoCiudad's
/// per-province download, read for <c>COD_POSTAL</c> and <c>INE_MUN</c>.
/// <para>
/// ⚠️ CNIG distributes CartoCiudad only through its download centre, behind an interactive form with no stable file URL
/// (checked 2026-10-19), so this source has <b>no automatic change check</b>: <see cref="CheckAsync"/> reports
/// "unchanged" and a scheduled run never queues it. The fixture is <b>doc-derived</b> (the CartoCiudad product
/// specification's field names, real postcodes of the cut's municipalities); the field names are unverified until the
/// first manual download (README).
/// </para>
/// </summary>
internal sealed class SpainCartoCiudadSource : RefFileSource
{
    public const string SourceId = "es-cartociudad";
    public override string Id => SourceId;
    public override string Country => "es";
    public override SourceLicence Licence => SpainLicences.CartoCiudad;
    public override IReadOnlyList<string> DownloadHosts => ["centrodedescargas.cnig.es"];
    protected override string DownloadUrl => "https://centrodedescargas.cnig.es/CentroDescargas/buscadorCatalogo.do?codFamilia=02122";
    protected override IReadOnlyList<string> FixtureNames => ["CARTOCIUDAD_codigo_postal.docderived.dbf"];

    public override Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
        => Task.FromResult<SourceVersion?>(null);

    public override Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
        => throw new NotSupportedException("es-cartociudad: CNIG offers CartoCiudad only through its download-centre form; download the per-province files by hand (README).");
}
