using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData.Sources.Poland;

/// <summary>Credit lines (PRD §4.4, §4.12; research §4).</summary>
internal static class PolandLicences
{
    /// <summary>
    /// GUS asks for "Źródło: GUS, TERYT, CC BY 4.0, stan na &lt;data&gt;" with the retrieval time. The source's version
    /// <em>is</em> the retrieval date (<c>yyyy-MM-dd</c>), recorded next to this line in every release manifest and at
    /// <c>/api/v1/sources</c>.
    /// </summary>
    public static readonly SourceLicence Teryt = new(
        "GUS TERYT", "CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/",
        "Źródło: GUS, TERYT, CC BY 4.0, stan na dzień pobrania (wersja źródła, rrrr-mm-dd). The data was modified: units, localities and streets are linked into a hierarchy.");

    /// <summary>
    /// PRG is "free for any use" without a licence name (GUGiK). Its postcodes are kept by the gminy and are <b>not
    /// validated by Poczta Polska</b> (whose PNA list is sold): the credit line says so, so every response with PL
    /// postcodes carries the caveat.
    /// </summary>
    public static readonly SourceLicence Prg = new(
        "GUGiK PRG address points", "Free for any use", "https://www.geoportal.gov.pl/pl/dane/panstwowy-rejestr-granic-prg/",
        "Źródło: Państwowy Rejestr Granic (PRG), GUGiK. Postcodes from the municipalities' address points, not validated by Poczta Polska. The data was modified: postcodes are linked to streets without house numbers.");
}

/// <summary>
/// GUS TERYT full files (<c>pl-teryt</c>): TERC (units), SIMC (localities) and ULIC (streets), "urzędowy" variant, as
/// CSV (UTF-8 with a BOM, <c>;</c>, CRLF) in ZIPs. The download page is an ASP.NET form: each file is a postback of
/// its button, with the page's hidden fields (the page is data, read for those fields only).
/// <para>
/// <b>Change check:</b> TERYT publishes no checksum and ULIC's <c>STAN_NA</c> is the download day itself, so the check
/// downloads the three files (≈12 MB) and hashes their rows without the <c>STAN_NA</c> column (RD1's last resort). The
/// <b>version is the retrieval date</b>, which the credit line's "stan na" refers to. The change service needs an
/// account and is not used.
/// </para>
/// </summary>
internal sealed partial class PolandTerytSource : RefFileSource
{
    public const string SourceId = "pl-teryt";
    public const string Page = "https://eteryt.stat.gov.pl/eTeryt/rejestr_teryt/udostepnianie_danych/baza_teryt/uzytkownicy_indywidualni/pobieranie/pliki_pelne.aspx";

    /// <summary>The form buttons of the three files.</summary>
    public static readonly string[] Buttons = ["ctl00$body$BTERCUrzedowyPobierz", "ctl00$body$BSIMCUrzedowyPobierz", "ctl00$body$BULICUrzedowyPobierz"];

    public override string Id => SourceId;
    public override string Country => "pl";
    public override SourceLicence Licence => PolandLicences.Teryt;
    public override IReadOnlyList<string> DownloadHosts => ["eteryt.stat.gov.pl"];
    public override string DefaultSchedule => "0 0 5 * * 1";
    protected override string DownloadUrl => Page;
    protected override IReadOnlyList<string> FixtureNames => ["TERC_Urzedowy_2026-10-19.cut.csv", "SIMC_Urzedowy_2026-10-19.cut.csv", "ULIC_Urzedowy_2026-10-19.cut.csv"];

    [GeneratedRegex(@"<input type=""hidden"" name=""(?<name>[^""]+)"" id=""[^""]*"" value=""(?<value>[^""]*)""")]
    private static partial Regex HiddenField();

    [GeneratedRegex(@"_(?<date>\d{4}-\d{2}-\d{2})\.zip")]
    private static partial Regex DatedName();

    [GeneratedRegex(@"^(TERC|SIMC|ULIC)_Urzedowy_\d{4}-\d{2}-\d{2}\.csv$")]
    internal static partial Regex CsvEntry();

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var files = await DownloadAsync(http, cancellationToken);
        var sha = ContentHash(files.Select(f => f.Csv));
        return last?.Sha256 == sha ? null : new SourceVersion(files[0].Date, Page, Sha256: sha);
    }

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var files = new List<SourceFile>();
        foreach (var (name, _, csv) in await DownloadAsync(http, cancellationToken))
        {
            var path = Path.Combine(workDirectory.FullName, RefZip.SafeName(name, "teryt.csv"));
            await File.WriteAllBytesAsync(path, csv, cancellationToken);
            files.Add(new SourceFile(Path.GetFileName(path), () => File.OpenRead(path)));
        }
        return new SourceInput
        {
            SourceId = Id, Version = version.Version, Url = Page, Sha256 = version.Sha256, Files = files,
            // "stan na <data>": the retrieval date, which is this version.
            Attribution = $"Źródło: GUS, TERYT, CC BY 4.0, stan na {version.Version}. The data was modified: units, localities and streets are linked into a hierarchy.",
        };
    }

    /// <summary>The rows of the three CSVs without their last column (<c>STAN_NA</c>), hashed: what changes the data, not the day.</summary>
    internal static string ContentHash(IEnumerable<byte[]> csvs)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var csv in csvs)
            foreach (var line in Encoding.UTF8.GetString(csv).Split('\n'))
            {
                var cut = line.TrimEnd('\r');
                var last = cut.LastIndexOf(';');
                sha.AppendData(Encoding.UTF8.GetBytes((last >= 0 ? cut[..last] : cut) + "\n"));
            }
        return "sha256:" + Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>The three CSVs: (entry name, retrieval date from the served file name, bytes). Each ZIP is capped at 64 MB.</summary>
    private static async Task<List<(string Name, string Date, byte[] Csv)>> DownloadAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var html = await http.GetStringAsync(Page, cancellationToken);
        var fields = HiddenField().Matches(html).ToDictionary(m => m.Groups["name"].Value, m => System.Net.WebUtility.HtmlDecode(m.Groups["value"].Value), StringComparer.Ordinal);
        var result = new List<(string, string, byte[])>();
        foreach (var button in Buttons)
        {
            var form = new Dictionary<string, string>(fields, StringComparer.Ordinal) { ["__EVENTTARGET"] = button, ["__EVENTARGUMENT"] = string.Empty };
            using var response = await http.PostAsync(Page, new FormUrlEncodedContent(form), cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 64L * 1024 * 1024)
                throw new InvalidDataException($"{SourceId}: {button} announces more than 64 MB.");
            var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? string.Empty;
            var date = DatedName().Match(fileName) is { Success: true } m ? m.Groups["date"].Value : throw new InvalidDataException($"{SourceId}: the download '{fileName}' carries no date.");
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => CsvEntry().IsMatch(e.Name)) ?? throw new InvalidDataException($"{SourceId}: {fileName} holds no CSV.");
            if (entry.Length > 256L * 1024 * 1024) throw new InvalidDataException($"{SourceId}: {entry.Name} is larger than 256 MB.");
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            result.Add((entry.Name, date, buffer.ToArray()));
        }
        return result;
    }
}

/// <summary>
/// The PRG address points (<c>pl-prg</c>): <c>POLSKA.zip</c> (≈564 MB) from GUGiK's open-data server, read only for
/// the attribute table <c>PRG_PunktyAdresowe_POLSKA.zip.dbf</c> (≈7 GB, <b>Deflate64</b>, which .NET reads and Python's
/// zipfile does not): postcode, street (<c>ID_ULIC</c> = ULIC's <c>SYM_UL</c>), locality (<c>ID_SIMC</c>) and gmina per
/// point. House numbers are not kept (RD5).
/// <para>
/// <b>Change check:</b> the server sends no <c>ETag</c> or <c>Last-Modified</c> and ignores range requests (checked
/// 2026-10-19), so the cheap check is the <c>Content-Length</c> (a weak validator: an update of the same size would be
/// missed until the next size change; the import itself compares contents by hash).
/// </para>
/// </summary>
internal sealed partial class PolandPrgSource : RefFileSource
{
    public const string SourceId = "pl-prg";
    public const string Url = "https://opendata.geoportal.gov.pl/prg/adresy/PunktyAdresowe/POLSKA.zip";

    public override string Id => SourceId;
    public override string Country => "pl";
    public override SourceLicence Licence => PolandLicences.Prg;
    public override IReadOnlyList<string> DownloadHosts => ["opendata.geoportal.gov.pl"];
    public override string DefaultSchedule => "0 0 3 * * 0";
    protected override string DownloadUrl => Url;
    protected override long MaxBytes => 2L * 1024 * 1024 * 1024;
    protected override IReadOnlyList<string> FixtureNames => ["PRG_PunktyAdresowe_POLSKA.cut.dbf"];

    [GeneratedRegex(@"^PRG_PunktyAdresowe_[A-Z_]+(\.zip)?\.dbf$")]
    internal static partial Regex PointsEntry();

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, Url);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var version = "len-" + (response.Content.Headers.ContentLength?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown");
        return last?.Version == version ? null : new SourceVersion(version, Url);
    }

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var zipPath = Path.Combine(workDirectory.FullName, "POLSKA.zip");
        var sha = await RefDownloads.DownloadAsync(http, Url, zipPath, MaxBytes, cancellationToken);
        string entryName;
        using (var zip = ZipFile.OpenRead(zipPath))
            entryName = zip.Entries.FirstOrDefault(e => PointsEntry().IsMatch(e.Name))?.FullName
                ?? throw new InvalidDataException($"{Id}: POLSKA.zip holds no address-point attribute table.");
        // Streamed straight from the archive: the 7 GB table is never extracted to disk.
        return new SourceInput
        {
            SourceId = Id, Version = version.Version, Url = Url, Sha256 = sha,
            Files = [new SourceFile(Path.GetFileName(entryName), () => ZipEntryStream.Open(zipPath, entryName))],
        };
    }
}

/// <summary>A ZIP entry's stream that closes its archive with it.</summary>
internal sealed class ZipEntryStream(ZipArchive archive, Stream inner) : Stream
{
    public static Stream Open(string zipPath, string entryName)
    {
        var archive = ZipFile.OpenRead(zipPath);
        return new ZipEntryStream(archive, archive.GetEntry(entryName)!.Open());
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { inner.Dispose(); archive.Dispose(); }
        base.Dispose(disposing);
    }
}
