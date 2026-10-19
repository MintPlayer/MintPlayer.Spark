using System.Text.Json;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData.Sources.Australia;

/// <summary>Credit lines and the G-NAF use restriction (PRD §4.4, §4.12).</summary>
internal static class AustraliaLicences
{
    /// <summary>
    /// The G-NAF End User Licence Agreement's use restriction (clause "Use restriction", read from the EULA PDF on
    /// data.gov.au on 2026-10-19, <c>20160226-eula-open-g-naf.pdf</c>). It passes to every user of the API (PRD §4.12):
    /// our API terms carry it, and so does every response's attribution.
    /// </summary>
    public const string MailRestriction =
        "You must not use the Licensed Material for the generation of an address or a compilation of addresses for the sending of mail " +
        "unless You have verified that each address to be used for the sending of mail is capable of receiving mail by reference to a " +
        "secondary source of information other than the Licensed Material.";

    public const string EulaUrl =
        "https://data.gov.au/data/dataset/19432f89-dc3a-4ef3-b943-5326ef1dbecc/resource/09f74802-08b1-4214-a6ea-3591b2753d30/download/20160226-eula-open-g-naf.pdf";

    public static readonly SourceLicence Gnaf = new(
        "Geoscape G-NAF", "Open G-NAF End User Licence Agreement", EulaUrl,
        "Incorporates or developed using G-NAF © Geoscape Australia licensed by the Commonwealth of Australia under the " +
        "Open Geo-coded National Address File (G-NAF) End User Licence Agreement. Use restriction: " + MailRestriction +
        " The data was modified: names are title-cased and street localities are linked to postcodes; house numbers are not kept.");
}

/// <summary>
/// Geoscape G-NAF (<c>au-gnaf</c>), quarterly (February, May, August, November): one ZIP of PSV files per state
/// (≈1.85 GB, GDA2020 edition). The change check asks data.gov.au's catalogue (CKAN <c>package_show</c>) for the
/// newest GDA2020 PSV resource; its URL names the release (<c>g-naf_aug26_…</c>), and its <c>ETag</c> is recorded.
/// Only the state, locality, street-locality and address-detail tables and two authority-code tables are extracted.
/// </summary>
/// <remarks>
/// data.gov.au answered automated requests (the catalogue API, HEAD and range requests on the ZIP) on 2026-10-19,
/// although research recorded refusals earlier (PRD §4.4). A host whose requests are refused cannot import G-NAF
/// automatically (documented in the README).
/// </remarks>
internal sealed partial class AustraliaGnafSource : RefFileSource
{
    public const string SourceId = "au-gnaf";
    private const string Package = "https://data.gov.au/data/api/3/action/package_show?id=19432f89-dc3a-4ef3-b943-5326ef1dbecc";

    public override string Id => SourceId;
    public override string Country => "au";
    public override SourceLicence Licence => AustraliaLicences.Gnaf;
    public override IReadOnlyList<string> DownloadHosts => ["data.gov.au"];
    public override string DefaultSchedule => "0 0 6 20 2,5,8,11 *";
    protected override string DownloadUrl => Package;
    protected override Regex? EntryPattern => GnafFile();
    protected override long MaxBytes => 4L * 1024 * 1024 * 1024;

    protected override IReadOnlyList<string> FixtureNames =>
    [
        "ACT_STATE_psv.psv", "ACT_LOCALITY_psv.psv", "ACT_STREET_LOCALITY_psv.psv", "ACT_ADDRESS_DETAIL_psv.psv",
        "Authority_Code_STREET_TYPE_AUT_psv.psv", "Authority_Code_LOCALITY_CLASS_AUT_psv.psv",
    ];

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        using var stream = await http.GetStreamAsync(Package, cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        // Release names (aug26, nov25) do not sort; the catalogue's creation stamps (ISO 8601) do.
        var zip = json.RootElement.GetProperty("result").GetProperty("resources").EnumerateArray()
            .Select(r => (Url: r.TryGetProperty("url", out var u) ? u.GetString() : null,
                          Created: r.TryGetProperty("created", out var c) ? c.GetString() ?? string.Empty : string.Empty))
            .Where(r => r.Url is not null && ReleaseZip().IsMatch(r.Url))
            .OrderByDescending(r => r.Created, StringComparer.Ordinal)
            .Select(r => r.Url)
            .FirstOrDefault()
            ?? throw new InvalidDataException($"{Id}: data.gov.au lists no GDA2020 PSV release.");
        if (!zip.StartsWith("https://data.gov.au/", StringComparison.Ordinal))
            throw new InvalidDataException($"{Id}: the release URL leaves data.gov.au.");
        var version = ReleaseZip().Match(zip).Groups["release"].Value;
        return last?.Version == version ? null : new SourceVersion(version, zip);
    }

    /// <summary>The release ZIP's name: <c>g-naf_aug26_allstates_gda2020_psv_110.zip</c> → <c>aug26</c>.</summary>
    [GeneratedRegex(@"/g-naf_(?<release>[a-z]{3}\d{2})_allstates_gda2020_psv_\d+\.zip$")]
    private static partial Regex ReleaseZip();

    /// <summary>The tables read: per state, and two authority-code tables.</summary>
    [GeneratedRegex(@"^([A-Z]{2,3}_(STATE|LOCALITY|STREET_LOCALITY|ADDRESS_DETAIL)|Authority_Code_(STREET_TYPE|LOCALITY_CLASS)_AUT)_psv\.psv$")]
    internal static partial Regex GnafFile();
}
