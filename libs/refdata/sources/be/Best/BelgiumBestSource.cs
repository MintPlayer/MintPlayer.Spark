using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData.Sources.Belgium.Best;

/// <summary>
/// The BeST Address full bundle (<c>be-best</c>, FPS BOSA, CC BY 4.0, weekly; PRD §4.4): municipalities, postal info,
/// street names (statuses, homonym additions, <c>streetname</c>/<c>hamlet</c>) and Walloon parts of municipality.
/// <list type="bullet">
/// <item><b>Change check:</b> <c>ETag</c>/<c>Last-Modified</c> on the bundle; when they moved, the ZIP central directory
/// (the last 64 KB, by range request) and the CRCs of the non-address entries. A bundle whose address files alone
/// changed is "no change" for us (RD5 imports no addresses).</item>
/// <item><b>Fetch:</b> only the non-address entries (≈3 MB of the 300+ MB bundle), each by range request (local header,
/// then the compressed bytes), inflated in-process and checked against its CRC; then the inner ZIP's XML.</item>
/// <item>Inner files are matched by pattern, never by name: from 2026-10-19 (Lambert 2008) the names drop <c>L72</c>.</item>
/// </list>
/// </summary>
internal sealed partial class BelgiumBestSource : IReferenceDataSource
{
    public const string SourceId = "be-best";
    public const string Url = "https://opendata.bosa.be/download/best/best-full-latest.zip";

    /// <summary>The bundle's inner ZIPs (PRD §4.4 pattern). Address entries match but are never fetched.</summary>
    [GeneratedRegex(@"^(?<region>Brussels|Flanders|Wallonia)(?<kind>Municipality|PostalInfo|PartOfMunicipality|StreetName|Address)(?<date>\d{8})(L72)?\.zip$")]
    internal static partial Regex InnerZip();

    /// <summary>The XML inside an inner ZIP, and the fixtures cut from them (<c>.cut.xml</c>).</summary>
    [GeneratedRegex(@"^(Brussels|Flanders|Wallonia)(Municipality|PostalInfo|PartOfMunicipality|StreetName)\d{8}(L72)?(\.cut)?\.xml$")]
    internal static partial Regex InnerXml();

    private const long MaxEntryBytes = 64L * 1024 * 1024;

    public string Id => SourceId;
    public string Country => "be";
    public SourceLicence Licence => BelgiumLicences.BeSt;
    public IReadOnlyList<string> DownloadHosts => ["opendata.bosa.be"];
    public string DefaultSchedule => "0 0 4 * * 0";

    public async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var conditional = await RefDownloads.CheckConditionalAsync(http, Url, last, cancellationToken);
        if (conditional is null)
            return null;
        var (entries, _) = await DirectoryAsync(http, cancellationToken);
        var version = ZipCentralDirectory.Fingerprint(Selected(entries));
        return last?.Version == version ? null : new SourceVersion(version, Url, conditional.ETag, conditional.LastModified);
    }

    public async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var (entries, length) = await DirectoryAsync(http, cancellationToken);
        var selected = Selected(entries).ToList();
        if (ZipCentralDirectory.Fingerprint(selected) != version.Version)
            throw new InvalidDataException("be-best: the bundle changed between the check and the fetch; the next run imports it.");

        var files = new List<SourceFile>();
        foreach (var entry in selected)
        {
            if (entry.CompressedSize > MaxEntryBytes || entry.UncompressedSize > MaxEntryBytes)
                throw new InvalidDataException($"be-best: {entry.Name} is larger than the {MaxEntryBytes}-byte cap.");
            var header = await RangeAsync(http, entry.LocalHeaderOffset, entry.LocalHeaderOffset + 29, length, cancellationToken);
            var dataStart = entry.LocalHeaderOffset + ZipCentralDirectory.LocalHeaderLength(header);
            byte[] compressed = entry.CompressedSize == 0 ? [] :await RangeAsync(http, dataStart, dataStart + entry.CompressedSize - 1, length, cancellationToken);

            var innerPath = Path.Combine(workDirectory.FullName, RefZip.SafeName(entry.Name, "entry.zip"));
            await using (var target = File.Create(innerPath))
            {
                var crc = 0u;
                long written = 0;
                await using Stream source = entry.Method switch
                {
                    0 => new MemoryStream(compressed),
                    8 => new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress),
                    _ => throw new InvalidDataException($"be-best: {entry.Name} uses compression method {entry.Method}."),
                };
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    written += read;
                    if (written > MaxEntryBytes)
                        throw new InvalidDataException($"be-best: {entry.Name} inflates beyond the {MaxEntryBytes}-byte cap.");
                    crc = ZipCrc32.Append(crc, buffer.AsSpan(0, read));
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (crc != entry.Crc32 || written != entry.UncompressedSize)
                    throw new InvalidDataException($"be-best: {entry.Name} does not match its CRC or size in the central directory.");
            }
            files.AddRange(RefZip.Extract(innerPath, InnerXml(), workDirectory, 1024L * 1024 * 1024));
        }
        return new SourceInput { SourceId = Id, Version = version.Version, Url = Url, Sha256 = null, Files = files };
    }

    public SourceInput Fixture() => new()
    {
        SourceId = Id,
        Version = "fixture",
        Files = [.. EmbeddedSourceFiles.Under(typeof(BelgiumBestSource).Assembly, "Fixtures/").Where(f => InnerXml().IsMatch(f.Name))],
    };

    /// <summary>The entries the import needs: every inner ZIP matching the pattern, except the address files (RD5).</summary>
    internal static IEnumerable<ZipDirectoryEntry> Selected(IEnumerable<ZipDirectoryEntry> entries)
        => entries.Where(e => InnerZip().Match(e.Name) is { Success: true } m && m.Groups["kind"].Value != "Address");

    private static async Task<(IReadOnlyList<ZipDirectoryEntry> Entries, long Length)> DirectoryAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Range = new RangeHeaderValue(null, ZipCentralDirectory.TailBytes);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.Length is not { } length)
            throw new InvalidDataException("be-best: the server did not honour the range request (refusing to download the whole bundle).");
        var tail = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var location = ZipCentralDirectory.Locate(tail, length);
        var tailStart = length - tail.Length;
        var directory = location.Offset >= tailStart
            ? tail.AsSpan((int)(location.Offset - tailStart), (int)location.Size).ToArray()
            : await RangeAsync(http, location.Offset, location.Offset + location.Size - 1, length, cancellationToken);
        return (ZipCentralDirectory.ReadEntries(directory, location.Count), length);
    }

    private static async Task<byte[]> RangeAsync(HttpClient http, long from, long to, long length, CancellationToken cancellationToken)
    {
        if (from < 0 || to < from || to >= length)
            throw new InvalidDataException($"be-best: the range {from}-{to} lies outside the {length}-byte bundle.");
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Range = new RangeHeaderValue(from, to);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new InvalidDataException("be-best: the server did not honour the range request.");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.LongLength != to - from + 1)
            throw new InvalidDataException($"be-best: the range {from}-{to} returned {bytes.LongLength} bytes.");
        return bytes;
    }
}
