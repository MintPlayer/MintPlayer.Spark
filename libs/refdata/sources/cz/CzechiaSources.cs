using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData.Sources.Czechia;

/// <summary>Credit lines (PRD §4.4, §4.12; research §4).</summary>
internal static class CzechiaLicences
{
    /// <summary>
    /// "ČÚZK, [rok]": the year is the year of the data (the state date of the RÚIAN file read), with the metadata and a
    /// link to the terms. ČÚZK's open data are published under CC BY 4.0.
    /// </summary>
    public static SourceLicence Ruian(int dataYear) => new(
        "ČÚZK RÚIAN", "CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/",
        $"ČÚZK, {dataYear.ToString(CultureInfo.InvariantCulture)}: Registr územní identifikace, adres a nemovitostí (RÚIAN), " +
        "via Veřejný dálkový přístup (https://vdp.cuzk.gov.cz/). Terms: https://cuzk.gov.cz/. The data was modified: units, streets and " +
        "postal codes are derived from the exchange-format files; house numbers are not kept. VDP is informational: binding data only through ISZR.");
}

/// <summary>
/// A RÚIAN file of VDP's monthly full state, named <c>{yyyyMMdd}_…</c> after the last day of the month it describes
/// (VDP moved from <c>vdp.cuzk.cz</c> to <c>vdp.cuzk.gov.cz</c>; the old host redirects). The change check is the
/// dated file name: the newest month-end whose file exists (a <c>HEAD</c>, two months back at most), compared with the
/// last version. VDP also sends <c>ETag</c>/<c>Last-Modified</c>, recorded with the version.
/// </summary>
internal abstract class RuianMonthlySource : RefFileSource
{
    public override string Country => "cz";
    public override SourceLicence Licence => CzechiaLicences.Ruian(DataYear);
    public override IReadOnlyList<string> DownloadHosts => ["vdp.cuzk.gov.cz", "vdp.cuzk.cz"];
    public override string DefaultSchedule => "0 0 5 2 * *";

    /// <summary>The year of the data last checked, fetched or read from the fixture (the fixture is the 2026-09-30 state).</summary>
    protected int DataYear { get; set; } = 2026;

    /// <summary>The URL of the file for one state date.</summary>
    protected abstract string UrlFor(DateOnly stateDate);

    protected override string DownloadUrl => UrlFor(LastMonthEnd(DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime)));

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var month = LastMonthEnd(DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime));
        for (var back = 0; back < 3; back++, month = LastMonthEnd(month.AddDays(1 - month.Day)))
        {
            var url = UrlFor(month);
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                continue;
            response.EnsureSuccessStatusCode();
            var version = month.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            DataYear = month.Year;
            return last?.Version == version
                ? null
                : new SourceVersion(version, url, response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
        }
        throw new InvalidDataException($"{Id}: no monthly RÚIAN file in the last three months.");
    }

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        if (DateOnly.TryParseExact(version.Version, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            DataYear = day.Year;
        var input = await base.FetchAsync(version, http, workDirectory, cancellationToken);
        // The credit line carries the year of this version's data ("ČÚZK, [rok]").
        return new SourceInput
        {
            SourceId = input.SourceId, Version = input.Version, Url = input.Url, Sha256 = input.Sha256, Files = input.Files,
            Attribution = CzechiaLicences.Ruian(DataYear).Attribution,
        };
    }

    /// <summary>The last day of the month before <paramref name="today"/>'s.</summary>
    internal static DateOnly LastMonthEnd(DateOnly today) => new DateOnly(today.Year, today.Month, 1).AddDays(-1);
}

/// <summary>
/// RÚIAN territorial structure (<c>cz-ruian-units</c>): the VFR file <c>{date}_ST_UZSZ.xml.zip</c> (≈4.7 MB) with the
/// state, the kraje (VÚSC), okresy, obce and části obce, each with its code, name, parent and <c>DatumVzniku</c>.
/// </summary>
internal sealed partial class CzechiaUnitsSource : RuianMonthlySource
{
    public const string SourceId = "cz-ruian-units";
    public override string Id => SourceId;
    protected override Regex? EntryPattern => UnitsFile();
    protected override long MaxBytes => 512L * 1024 * 1024;
    protected override IReadOnlyList<string> FixtureNames => ["20260930_ST_UZSZ.cut.xml"];
    protected override string UrlFor(DateOnly stateDate) => $"https://vdp.cuzk.gov.cz/vymenny_format/soucasna/{stateDate:yyyyMMdd}_ST_UZSZ.xml.zip";

    [GeneratedRegex(@"^\d{8}_ST_UZSZ(\.cut)?\.xml$")]
    internal static partial Regex UnitsFile();
}

/// <summary>
/// RÚIAN address places (<c>cz-ruian-addresses</c>): the national bundle <c>{date}_OB_ADR_csv.zip</c> (≈63 MB) with one
/// CSV per obec (<c>{date}_OB_{kód obce}_ADR.csv</c>; Windows-1250, <c>;</c>, LF). Streamed only for the street and PSČ
/// of each address place: streets ↔ postal codes come from address points (RD5: no house numbers are kept).
/// </summary>
internal sealed partial class CzechiaAddressesSource : RuianMonthlySource
{
    public const string SourceId = "cz-ruian-addresses";
    public override string Id => SourceId;
    protected override Regex? EntryPattern => AddressFile();
    protected override long MaxBytes => 2L * 1024 * 1024 * 1024;
    protected override IReadOnlyList<string> FixtureNames => ["20260930_OB_563889_ADR.cut.csv", "20260930_OB_563943_ADR.cut.csv"];
    protected override string UrlFor(DateOnly stateDate) => $"https://vdp.cuzk.gov.cz/vymenny_format/csv/{stateDate:yyyyMMdd}_OB_ADR_csv.zip";

    [GeneratedRegex(@"^\d{8}_OB_\d{6}_ADR(\.cut)?\.csv$")]
    internal static partial Regex AddressFile();
}
