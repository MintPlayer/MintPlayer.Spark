using System.IO.Compression;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData.Sources.France;

/// <summary>Credit lines (PRD §4.4, §4.12; research §4).</summary>
internal static class FranceLicences
{
    /// <summary>"Source : Insee" with the date of last update (the edition), as INSEE asks; INSEE forbids implied endorsement.</summary>
    public static readonly SourceLicence Insee = new(
        "Insee, Code officiel géographique", "Licence Ouverte 2.0", "https://www.etalab.gouv.fr/licence-ouverte-open-licence/",
        "Source : Insee, Code officiel géographique (millésime de la dernière mise à jour). Données modifiées : périodes, hiérarchie et filiation dérivées.");

    public static readonly SourceLicence Ban = new(
        "Base Adresse Nationale (BAN)", "Licence Ouverte 2.0", "https://www.etalab.gouv.fr/licence-ouverte-open-licence/",
        "Source : Base Adresse Nationale (adresse.data.gouv.fr), Licence Ouverte 2.0. Données modifiées : voies et codes postaux extraits des adresses.");

    public static readonly SourceLicence LaPoste = new(
        "La Poste, base officielle des codes postaux", "Licence Ouverte 2.0", "https://www.etalab.gouv.fr/licence-ouverte-open-licence/",
        "Source : La Poste, base officielle des codes postaux (datanova), Licence Ouverte 2.0. Données modifiées : localités liées aux communes.");
}

/// <summary>
/// INSEE's Code officiel géographique (<c>fr-cog</c>): regions, départements and communes of the edition, every commune
/// since 1943 with its validity (<c>v_commune_depuis_1943</c>) and every mouvement (<c>v_mvt_commune</c>). Published
/// yearly on 1 January. The edition is discovered on INSEE's COG page ("Millésime {yyyy} : Téléchargement des
/// fichiers"); the change check is the millésime. Never api-adresse (decommissioned; its successor is rate-limited).
/// </summary>
internal sealed partial class FranceCogSource : RefFileSource
{
    public const string SourceId = "fr-cog";
    public const string Index = "https://www.insee.fr/fr/information/2560452";

    public override string Id => SourceId;
    public override string Country => "fr";
    public override SourceLicence Licence => FranceLicences.Insee;
    public override IReadOnlyList<string> DownloadHosts => ["www.insee.fr"];
    public override string DefaultSchedule => "0 0 5 * * 1";
    protected override string DownloadUrl => Index;
    protected override long MaxBytes => 64L * 1024 * 1024;

    protected override IReadOnlyList<string> FixtureNames =>
    [
        "v_region_2026.cut.csv", "v_departement_2026.cut.csv", "v_commune_2026.cut.csv", "v_commune_depuis_1943.cut.csv", "v_mvt_commune_2026.cut.csv",
    ];

    /// <summary>"Millésime 2026&amp;nbsp;: &lt;a … href="/fr/information/8740222"&gt;Téléchargement des fichiers". ⚠️ Page markup verified on 2026-10-19 only.</summary>
    [GeneratedRegex(@"Mill[ée]sime\s+(?<year>\d{4})(?:&nbsp;|\s)*:\s*<a[^>]+href=""(?<page>/fr/information/\d+)""[^>]*>\s*T[ée]l[ée]chargement des fichiers", RegexOptions.IgnoreCase)]
    private static partial Regex Edition();

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var html = await http.GetStringAsync(Index, cancellationToken);
        var latest = Edition().Matches(html).Select(m => (Year: m.Groups["year"].Value, Page: m.Groups["page"].Value))
            .OrderBy(e => e.Year, StringComparer.Ordinal).LastOrDefault();
        if (latest.Year is null)
            throw new InvalidDataException($"{SourceId}: no COG edition found on {Index}.");
        return last?.Version == latest.Year ? null : new SourceVersion(latest.Year, "https://www.insee.fr" + latest.Page);
    }

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        // The edition page's file URLs: /fr/statistiques/fichier/{page}/v_commune_{yyyy}.csv and the others.
        var page = new Uri(version.Url).Segments[^1];
        var year = version.Version;
        var files = new List<SourceFile>();
        foreach (var name in new[] { $"v_region_{year}.csv", $"v_departement_{year}.csv", $"v_commune_{year}.csv", "v_commune_depuis_1943.csv", $"v_mvt_commune_{year}.csv" })
        {
            var path = Path.Combine(workDirectory.FullName, name);
            await RefDownloads.DownloadAsync(http, $"https://www.insee.fr/fr/statistiques/fichier/{page}/{name}", path, MaxBytes, cancellationToken);
            files.Add(new SourceFile(name, () => File.OpenRead(path)));
        }
        return new SourceInput { SourceId = Id, Version = version.Version, Url = version.Url, Files = files };
    }
}

/// <summary>
/// The Base Adresse Nationale (<c>fr-ban</c>): the per-département address CSV files (never the national 938 MB file in
/// one piece, never an API), streamed for streets and postcodes; no house number is kept (RD5). The change check is the
/// national file's <c>ETag</c>/<c>Last-Modified</c> (all files are produced together).
/// </summary>
internal sealed class FranceBanSource : RefFileSource
{
    public const string SourceId = "fr-ban";
    public const string Base = "https://adresse.data.gouv.fr/data/ban/adresses/latest/csv/";

    /// <summary>Every département and overseas département of the COG (Corsica's 2A/2B are not numeric).</summary>
    public static readonly string[] Departements =
    [
        .. Enumerable.Range(1, 95).Where(d => d != 20).Select(d => d.ToString("00", System.Globalization.CultureInfo.InvariantCulture)),
        "2A", "2B", "971", "972", "973", "974", "976",
    ];

    public override string Id => SourceId;
    public override string Country => "fr";
    public override SourceLicence Licence => FranceLicences.Ban;
    public override IReadOnlyList<string> DownloadHosts => ["adresse.data.gouv.fr"];
    public override string DefaultSchedule => "0 30 5 * * 0";
    protected override string DownloadUrl => Base + "adresses-france.csv.gz";
    protected override long MaxBytes => 2L * 1024 * 1024 * 1024;
    protected override IReadOnlyList<string> FixtureNames => ["adresses-01.cut.csv", "adresses-2A.cut.csv"];

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var files = new List<SourceFile>();
        foreach (var departement in Departements)
        {
            var gz = Path.Combine(workDirectory.FullName, $"adresses-{departement}.csv.gz");
            await RefDownloads.DownloadAsync(http, $"{Base}adresses-{departement}.csv.gz", gz, MaxBytes, cancellationToken);
            var csv = Path.Combine(workDirectory.FullName, $"adresses-{departement}.csv");
            await using (var source = new GZipStream(File.OpenRead(gz), CompressionMode.Decompress))
            await using (var target = File.Create(csv))
                await source.CopyToAsync(target, cancellationToken);
            File.Delete(gz);
            files.Add(new SourceFile(Path.GetFileName(csv), () => File.OpenRead(csv)));
        }
        return new SourceInput { SourceId = Id, Version = version.Version, Url = Base, Files = files };
    }
}

/// <summary>
/// La Poste's base officielle des codes postaux (<c>fr-laposte</c>, "laposte-hexasmal", ≈1.5 MB, Licence Ouverte 2.0
/// per data.gouv.fr): (commune, postcode, libellé d'acheminement, ligne 5) rows. ISO-8859-1, semicolons. The change
/// check is <c>Last-Modified</c>.
/// </summary>
internal sealed class FranceLaPosteSource : RefFileSource
{
    public const string SourceId = "fr-laposte";

    public override string Id => SourceId;
    public override string Country => "fr";
    public override SourceLicence Licence => FranceLicences.LaPoste;
    public override IReadOnlyList<string> DownloadHosts => ["data.laposte.fr"];
    public override string DefaultSchedule => "0 15 5 * * 1";
    protected override string DownloadUrl => "https://data.laposte.fr/data-fair/api/v1/datasets/laposte-hexasmal/raw";
    protected override long MaxBytes => 64L * 1024 * 1024;
    protected override IReadOnlyList<string> FixtureNames => ["laposte_hexasmal.cut.csv"];

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(workDirectory.FullName, "laposte_hexasmal.csv");
        var sha = await RefDownloads.DownloadAsync(http, DownloadUrl, path, MaxBytes, cancellationToken);
        return new SourceInput { SourceId = Id, Version = version.Version, Url = DownloadUrl, Sha256 = sha, Files = [new SourceFile("laposte_hexasmal.csv", () => File.OpenRead(path))] };
    }
}
