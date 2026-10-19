using System.IO.Compression;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData.Sources.Belgium;

/// <summary>Credit lines (PRD §4.12; research §4).</summary>
internal static class BelgiumLicences
{
    public static readonly SourceLicence Statbel = new(
        "Statbel", "CC BY 4.0", "https://statbel.fgov.be/en/cc-40",
        "Source: Statbel (https://statbel.fgov.be/en/cc-40). The data was modified: periods, hierarchy and lineage are derived from it.");

    public static readonly SourceLicence BeSt = new(
        "FPS BOSA BeST Address", "CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/",
        "Digitaal Vlaanderen (Vlaams Gewest), Paradigm (Brussels Hoofdstedelijk Gewest), Service public de Wallonie (Waals Gewest), " +
        "via FOD BOSA BeST Address (https://opendata.bosa.be/). The data was modified: names are split per locality and linked to places.");
}

/// <summary>A source whose edition is a dated file name found on the publisher's page (Statbel publishes no ETag or checksum).</summary>
internal abstract partial class DatedFileSource : IReferenceDataSource
{
    public abstract string Id { get; }
    public string Country => "be";
    public abstract SourceLicence Licence { get; }
    public IReadOnlyList<string> DownloadHosts => ["statbel.fgov.be"];
    public string DefaultSchedule => "0 30 4 * * 1";

    /// <summary>The page that links the current edition. ⚠️ Verified for the file URLs, not for the page itself (RS8).</summary>
    protected abstract string Page { get; }
    protected abstract Regex FilePattern { get; }
    protected abstract string FixtureName { get; }

    public async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var html = await http.GetStringAsync(Page, cancellationToken);
        var editions = FilePattern.Matches(html).Select(m => (Url: m.Value, Date: m.Groups["date"].Value)).OrderBy(e => e.Date, StringComparer.Ordinal).ToList();
        if (editions.Count == 0)
            throw new InvalidDataException($"{Id}: no edition matching {FilePattern} on {Page}.");
        var latest = editions[^1];
        if (last?.Version == latest.Date)
            return null;
        var url = latest.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? latest.Url : new Uri(new Uri(Page), latest.Url).ToString();
        return new SourceVersion(latest.Date, url);
    }

    public async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var target = Path.Combine(workDirectory.FullName, Path.GetFileName(new Uri(version.Url).LocalPath));
        var sha = await RefDownloads.DownloadAsync(http, version.Url, target, 64L * 1024 * 1024, cancellationToken);
        var files = new List<SourceFile>();
        if (target.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(target);
            foreach (var entry in zip.Entries.Where(e => e.Length > 0))
            {
                var path = Path.Combine(workDirectory.FullName, Path.GetFileName(entry.FullName));
                entry.ExtractToFile(path, overwrite: true);
                files.Add(new SourceFile(Path.GetFileName(path), () => File.OpenRead(path)));
            }
        }
        else
            files.Add(new SourceFile(Path.GetFileName(target), () => File.OpenRead(target)));
        return new SourceInput { SourceId = Id, Version = version.Version, Url = version.Url, Sha256 = sha, Files = files };
    }

    public SourceInput Fixture() => new()
    {
        SourceId = Id,
        Version = "fixture",
        Files = [.. EmbeddedSourceFiles.Under(typeof(DatedFileSource).Assembly, "Fixtures/").Where(f => f.Name == FixtureName)],
    };
}

/// <summary>
/// Statbel REFNIS (<c>be-refnis</c>): every region, province, arrondissement and municipality code since 1970 with
/// validity dates and nl/fr/de names. The primary source for Belgian admin units and their dates (Q-R3).
/// </summary>
internal sealed partial class BelgiumRefnisSource : DatedFileSource
{
    public const string SourceId = "be-refnis";
    public override string Id => SourceId;
    public override SourceLicence Licence => BelgiumLicences.Statbel;
    protected override string Page => "https://statbel.fgov.be/en/about-statbel/what-we-do/standards/refnis-code";
    protected override Regex FilePattern => Pattern();
    protected override string FixtureName => "TU_COM_REFNIS-20250101.cut.txt";

    [GeneratedRegex(@"https?://statbel\.fgov\.be/sites/default/files/files/opendata/REFNIS%20code/TU_COM_REFNIS-(?<date>\d{8})\.zip")]
    private static partial Regex Pattern();
}

/// <summary>Statbel NIS6 (<c>be-nis6</c>): the 2,616 sections (deelgemeenten) with their current municipality.</summary>
internal sealed partial class BelgiumNis6Source : DatedFileSource
{
    public const string SourceId = "be-nis6";
    public override string Id => SourceId;
    public override SourceLicence Licence => BelgiumLicences.Statbel;
    protected override string Page => "https://statbel.fgov.be/en/open-data/statistical-sectors-2025";
    protected override Regex FilePattern => Pattern();
    protected override string FixtureName => "NIS6withnamefrom01012025.cut.xlsx";

    [GeneratedRegex(@"https?://statbel\.fgov\.be/sites/default/files/files/opendata/Statistische%20sectoren/NIS6withnamefrom(?<date>\d{8})\.xlsx")]
    private static partial Regex Pattern();
}

/// <summary>
/// BeST postal streets (<c>be-postalstreets</c>): ≈147k (postcode, street) pairs, the street ↔ postcode links, the
/// Walloon parts of municipality and the postal localities (RD-DEF 9: never bpost). Change check: <c>ETag</c>.
/// </summary>
internal sealed class BelgiumPostalStreetsSource : IReferenceDataSource
{
    public const string SourceId = "be-postalstreets";
    public const string Url = "https://opendata.bosa.be/download/best/postalstreets-latest.zip";

    public string Id => SourceId;
    public string Country => "be";
    public SourceLicence Licence => BelgiumLicences.BeSt;
    public IReadOnlyList<string> DownloadHosts => ["opendata.bosa.be"];
    public string DefaultSchedule => "0 0 4 * * *";

    public Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
        => RefDownloads.CheckConditionalAsync(http, Url, last, cancellationToken);

    public async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var zipPath = Path.Combine(workDirectory.FullName, "postalstreets.zip");
        var sha = await RefDownloads.DownloadAsync(http, Url, zipPath, 64L * 1024 * 1024, cancellationToken);
        var files = new List<SourceFile>();
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries.Where(e => BelgiumMapper.PostalStreetFile.IsMatch(e.Name)))
            {
                if (entry.Length > 256L * 1024 * 1024)
                    throw new InvalidDataException($"{entry.Name} is larger than the 256 MB cap.");
                var path = Path.Combine(workDirectory.FullName, entry.Name);
                entry.ExtractToFile(path, overwrite: true);
                files.Add(new SourceFile(entry.Name, () => File.OpenRead(path)));
            }
        }
        return new SourceInput { SourceId = Id, Version = version.Version, Url = Url, Sha256 = sha, Files = files };
    }

    public SourceInput Fixture() => new()
    {
        SourceId = Id,
        Version = "fixture",
        Files = [.. EmbeddedSourceFiles.Under(typeof(BelgiumPostalStreetsSource).Assembly, "Fixtures/").Where(f => BelgiumMapper.PostalStreetFile.IsMatch(f.Name))],
    };
}
