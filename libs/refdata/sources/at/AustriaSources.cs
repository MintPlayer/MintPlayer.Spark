using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData.Sources.Austria;

/// <summary>Credit lines (PRD §4.4, §4.12; research §4).</summary>
internal static class AustriaLicences
{
    /// <summary>
    /// BEV's terms (<c>Nutzungsbedingungen.txt</c> in the download) grant unlimited use on condition that every release
    /// quotes "© Österreichisches Adressregister, Stichtagsdaten vom TT.MM.JJJJ"; the product page names CC BY 4.0. The
    /// record date is the source version of the release (the manifest's <c>Sources[at-bev-adressregister].Version</c>).
    /// </summary>
    public static readonly SourceLicence Bev = new(
        "BEV Adressregister", "CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/",
        "© Österreichisches Adressregister, Stichtagsdaten vom <record date: this source's version>. The data was modified: streets are linked to postcodes through the addresses; no house numbers are kept.");

    public static readonly SourceLicence Statistik = new(
        "Statistik Austria", "CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/",
        "Q: STATISTIK AUSTRIA. The data was modified: periods and lineage are derived from its change file.");
}

/// <summary>
/// BEV's Adressregister relational tables (<c>at-bev-adressregister</c>): GEMEINDE, ORTSCHAFT, STRASSE and ADRESSE
/// (postcode per address) as CSV in one ZIP, generated twice a year (Q2 and Q4) and offered 14 days later; the paid API
/// (€480 a year per external application) is not used. Change check: the ZIP's <c>ETag</c>, so most scheduled checks
/// report "no change". The version is the record date (Stichtag) in <c>Aktualitaetsstand.txt</c>, which the credit line
/// must quote.
/// </summary>
internal sealed partial class AustriaAddressRegisterSource : RefFileSource
{
    public const string SourceId = "at-bev-adressregister";
    public override string Id => SourceId;
    public override string Country => "at";
    public override SourceLicence Licence => AustriaLicences.Bev;
    public override IReadOnlyList<string> DownloadHosts => ["data.bev.gv.at"];
    public override string DefaultSchedule => "0 0 6 * * 1";
    protected override string DownloadUrl => "https://data.bev.gv.at/download/Adressregister/Adresse_Relationale_Tabellen_Stichtagsdaten.zip";
    protected override Regex? EntryPattern => Entries();
    protected override long MaxBytes => 2L * 1024 * 1024 * 1024;
    protected override IReadOnlyList<string> FixtureNames => ["GEMEINDE.cut.csv", "ORTSCHAFT.cut.csv", "STRASSE.cut.csv", "ADRESSE.cut.csv", "Aktualitaetsstand.txt"];

    [GeneratedRegex(@"^((GEMEINDE|ORTSCHAFT|STRASSE|ADRESSE)(\.cut)?\.csv|Aktualitaetsstand\.txt)$")]
    private static partial Regex Entries();

    [GeneratedRegex(@"(?<d>\d{2})\.(?<m>\d{2})\.(?<y>\d{4})")]
    private static partial Regex RecordDate();

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var input = await base.FetchAsync(version, http, workDirectory, cancellationToken);
        var stichtag = StichtagOf(input);
        return new SourceInput
        {
            SourceId = input.SourceId,
            Version = stichtag ?? input.Version,
            Url = input.Url,
            Sha256 = input.Sha256,
            Files = input.Files,
            // The BEV terms ask for the record date in the credit (SourceInput.Attribution, the kit's dated-credit hook).
            Attribution = stichtag is { } day
                ? $"© Österreichisches Adressregister, Stichtagsdaten vom {DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture):dd.MM.yyyy}. The data was modified: streets are linked to postcodes through the addresses; no house numbers are kept."
                : null,
        };
    }

    /// <summary>The record date in <c>Aktualitaetsstand.txt</c> (Windows-1252), as <c>yyyy-MM-dd</c>.</summary>
    internal static string? StichtagOf(SourceInput input)
    {
        if (input.Files.FirstOrDefault(f => f.Name == "Aktualitaetsstand.txt") is not { } file)
            return null;
        using var reader = new StreamReader(file.Open(), Encoding.Latin1);
        return RecordDate().Match(reader.ReadToEnd()) is { Success: true } m
            ? new DateOnly(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;
    }
}

/// <summary>
/// Statistik Austria's regional lists (<c>at-statistik-regionen</c>, <c>reglisten.zip</c>): <c>polbezirke.csv</c>,
/// the Bundesländer and political districts with their codes. Change check: <c>ETag</c>.
/// </summary>
internal sealed partial class AustriaRegionsSource : RefFileSource
{
    public const string SourceId = "at-statistik-regionen";
    public override string Id => SourceId;
    public override string Country => "at";
    public override SourceLicence Licence => AustriaLicences.Statistik;
    public override IReadOnlyList<string> DownloadHosts => ["www.statistik.at"];
    public override string DefaultSchedule => "0 0 6 1 * *";
    protected override string DownloadUrl => "https://www.statistik.at/verzeichnis/reglisten/reglisten.zip";
    protected override Regex? EntryPattern => Entries();
    protected override long MaxBytes => 256L * 1024 * 1024;
    protected override IReadOnlyList<string> FixtureNames => ["polbezirke.cut.csv"];

    [GeneratedRegex(@"^polbezirke(\.cut)?\.csv$")]
    internal static partial Regex Entries();
}

/// <summary>
/// Statistik Austria's change file (<c>at-statistik-aenderungen</c>, <c>aenderungenVerwaltungsgliederung.zip</c>):
/// <c>aenderungenGemeinden_ab2002.ods</c> with sheets for code changes, mergers, splits, district mergers (recodes),
/// renames and status changes since 2002. Change check: <c>ETag</c>.
/// </summary>
internal sealed partial class AustriaChangesSource : RefFileSource
{
    public const string SourceId = "at-statistik-aenderungen";
    public override string Id => SourceId;
    public override string Country => "at";
    public override SourceLicence Licence => AustriaLicences.Statistik;
    public override IReadOnlyList<string> DownloadHosts => ["www.statistik.at"];
    public override string DefaultSchedule => "0 15 6 1 * *";
    protected override string DownloadUrl => "https://www.statistik.at/fileadmin/pages/453/aenderungenVerwaltungsgliederung.zip";
    protected override Regex? EntryPattern => Entries();
    protected override long MaxBytes => 256L * 1024 * 1024;
    protected override IReadOnlyList<string> FixtureNames => ["aenderungenGemeinden_ab2002.cut.ods"];

    [GeneratedRegex(@"^aenderungenGemeinden_ab\d{4}(\.cut)?\.ods$")]
    internal static partial Regex Entries();
}
