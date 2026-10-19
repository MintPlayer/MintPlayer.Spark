using System.Text;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData.Sources.Global;

/// <summary>Credit lines of the name sources (PRD §4.4, §4.12).</summary>
internal static class GlobalNameLicences
{
    /// <summary>
    /// GNS states no restrictions and asks for a citation. ⚠️ The citation below is NGA's standard GNS credit as
    /// recalled: geonames.nga.mil serves its pages through a script and the text could not be copied on 2026-10-19;
    /// verify it against the site before a release.
    /// </summary>
    public static readonly SourceLicence Gns = new(
        "NGA GEOnet Names Server", "No restrictions (citation requested)", "https://geonames.nga.mil/",
        "Toponymic information is based on the Geographic Names Database, containing official standard names approved by the " +
        "United States Board on Geographic Names and maintained by the National Geospatial-Intelligence Agency. More information " +
        "is available at https://geonames.nga.mil/. The National Geospatial-Intelligence Agency name, initials, and seal are " +
        "protected by 10 United States Code § 425.");

    public static readonly SourceLicence UnLocode = new(
        "UN/LOCODE (UNECE)", "CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/",
        "UN/LOCODE, United Nations Economic Commission for Europe (https://unlocode.unece.org/), CC BY 4.0. The data was modified: " +
        "location codes are linked to places by name.");

    public static readonly SourceLicence IanaSubtags = new(
        "IANA Language Subtag Registry", "CC0 1.0", "https://www.iana.org/help/licensing-terms",
        "Language, script and region subtags from the IANA Language Subtag Registry (https://www.iana.org/assignments/language-subtag-registry), " +
        "used instead of SIL's ISO 639-3 tables; credited although not required.", Modified: false);
}

/// <summary>
/// NGA GNS (<c>global-gns</c>): BGN/PCGN-approved names with their name type (<c>N</c> approved, <c>C</c> conventional,
/// <c>V</c> variant), language and script, world minus the US. The per-country files of the GNS data page; the change
/// check is the conditional request on the first one (they are regenerated together).
/// </summary>
/// <remarks>
/// On 2026-10-19 every per-country file held only its header (the data page lists 0 features for 269 of 270
/// entities), so the fixture is <b>doc-derived</b>: the real header, rows written to the published schema.
/// </remarks>
internal sealed partial class GnsSource : RefFileSource
{
    public const string SourceId = "global-gns";

    /// <summary>The GNS file names of the countries the baseline covers (GNS names files by English country name).</summary>
    public static readonly string[] CountryFiles = ["Belgium", "France", "Germany", "Luxembourg", "Netherlands"];

    [GeneratedRegex(@"^(Belgium|France|Germany|Luxembourg|Netherlands)\.txt$")]
    private static partial Regex CountryFile();

    public override string Id => SourceId;
    public override string Country => "*";
    public override SourceLicence Licence => GlobalNameLicences.Gns;
    public override IReadOnlyList<string> DownloadHosts => ["geonames.nga.mil"];
    public override string DefaultSchedule => "0 0 6 * * 0";
    protected override string DownloadUrl => $"https://geonames.nga.mil/geonames/GNSData/{CountryFiles[0]}.zip";
    protected override IReadOnlyList<string> FixtureNames => ["gns-adm1.doc-derived.txt"];

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var files = new List<SourceFile>();
        foreach (var country in CountryFiles)
        {
            var zip = Path.Combine(workDirectory.FullName, country + ".zip");
            await RefDownloads.DownloadAsync(http, $"https://geonames.nga.mil/geonames/GNSData/{country}.zip", zip, 512L * 1024 * 1024, cancellationToken);
            files.AddRange(RefZip.Extract(zip, CountryFile(), workDirectory, 2L * 1024 * 1024 * 1024));
        }
        return new SourceInput { SourceId = Id, Version = version.Version, Url = DownloadUrl, Files = files };
    }
}

/// <summary>
/// UN/LOCODE (<c>global-unlocode</c>, UNECE, CC BY 4.0): trade-location codes with their names, subdivision codes, and
/// the <c>=</c> reference rows that give conventional names ("Antwerp = Antwerpen"). The release zip's CSV files only
/// (UTF-8, no header; the MDB, TTL and XML are never extracted).
/// </summary>
internal sealed partial class UnLocodeSource : RefFileSource
{
    public const string SourceId = "global-unlocode";

    [GeneratedRegex(@"^(UNLOCODE ?CodeListPart\d|SubdivisionCodes)\.csv$")]
    private static partial Regex CsvFiles();

    public override string Id => SourceId;
    public override string Country => "*";
    public override SourceLicence Licence => GlobalNameLicences.UnLocode;
    public override IReadOnlyList<string> DownloadHosts => ["unlocode.unece.org"];
    public override string DefaultSchedule => "0 0 6 * * 0";
    protected override string DownloadUrl => "https://unlocode.unece.org/downloads/unlocode-latest.zip";
    protected override Regex? EntryPattern => CsvFiles();
    protected override long MaxBytes => 512L * 1024 * 1024;
    protected override IReadOnlyList<string> FixtureNames => ["unlocode-codelist.cut.csv", "unlocode-subdivisions.cut.csv"];
}

/// <summary>
/// The IANA Language Subtag Registry (<c>global-iana-subtags</c>, CC0): every language, script and region subtag with
/// its deprecations. The change check is its <c>File-Date</c> (the first line), read without downloading the file.
/// </summary>
internal sealed class IanaSubtagSource : RefFileSource
{
    public const string SourceId = "global-iana-subtags";
    public const string RegistryUrl = "https://www.iana.org/assignments/language-subtag-registry/language-subtag-registry";

    public override string Id => SourceId;
    public override string Country => "*";
    public override SourceLicence Licence => GlobalNameLicences.IanaSubtags;
    public override IReadOnlyList<string> DownloadHosts => ["www.iana.org"];
    public override string DefaultSchedule => "0 0 6 * * 0";
    protected override string DownloadUrl => RegistryUrl;
    protected override long MaxBytes => 16L * 1024 * 1024;
    protected override IReadOnlyList<string> FixtureNames => ["language-subtag-registry.cut.txt"];

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RegistryUrl);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 255);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var first = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
        var date = IanaSubtags.ReadFileDate(first) ?? throw new InvalidDataException($"{SourceId}: the registry does not start with File-Date.");
        return last?.Version == date ? null : new SourceVersion(date, RegistryUrl);
    }
}
