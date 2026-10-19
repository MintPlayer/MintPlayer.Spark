using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData.Sources.Denmark;

/// <summary>
/// Credit lines (PRD §4.4, §4.12). The Danish credit changes on <b>2026-12-01</b>: "(CC BY 4.0) KDS" until then,
/// "DMI, Kort og Grunddata" from then on (research §2). Which one applies is decided by the <em>data version's</em>
/// date, never by the clock (K10): a release built later from an older download keeps the older credit.
/// </summary>
internal static class DenmarkLicences
{
    public static readonly DateOnly CreditChange = new(2026, 12, 1);

    public static SourceLicence For(string dataset, DateOnly? versionDate) => new(
        $"Datafordeler {dataset}", "CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/",
        (versionDate is { } day && day >= CreditChange ? "DMI, Kort og Grunddata" : "(CC BY 4.0) KDS")
        + $" — {dataset} via Datafordeleren (https://datafordeler.dk). CC BY 4.0 covers downloads from 2024-05-16. The data was modified: units and streets are linked into a dated hierarchy.");
}

/// <summary>
/// A Datafordeler file download (DAR, DAGI): a JSON document in a ZIP (a full file every Saturday night; deltas are
/// not used). <b>An account is required, and from 2027-01-15 only API-key or OAuth access</b>: the key is a host
/// secret, read from <c>Spark:ReferenceData:Sources:{id}:ApiKey</c> (user-secrets) when a download runs, sent as a
/// request header and never written to a URL, a log or a document.
/// </summary>
/// <remarks>
/// ⚠️ Unverified from here (Datafordeler answers 401 without an account, 2026-10-19): the file-download query
/// (<see cref="DownloadQuery"/>) and the key header name (<see cref="ApiKeyHeader"/>) follow Datafordeler's file-download
/// documentation as summarized in research §2 and must be checked against a real account (RS8).
/// </remarks>
internal abstract class DatafordelerSource : IReferenceDataSource
{
    public const string FileDownloads = "https://api.datafordeler.dk/FileDownloads/GetFile";
    public const string ApiKeyHeader = "X-API-KEY";

    /// <summary>The key, from configuration (set by <c>AddDenmark()</c>). Never logged.</summary>
    internal Func<string?> ApiKey { get; set; } = () => null;

    public abstract string Id { get; }
    public string Country => "dk";
    protected abstract string Dataset { get; }
    protected abstract string DownloadQuery { get; }
    protected abstract string FixtureName { get; }
    protected abstract DateOnly FixtureDate { get; }
    /// <summary>The undated licence; each input carries the credit of its own version's date (<see cref="SourceInput.Attribution"/>).</summary>
    public SourceLicence Licence => DenmarkLicences.For(Dataset, null);
    public IReadOnlyList<string> DownloadHosts => ["api.datafordeler.dk"];
    public abstract string DefaultSchedule { get; }

    private string Url => FileDownloads + "?" + DownloadQuery;

    private HttpRequestMessage Request(HttpMethod method)
    {
        var request = new HttpRequestMessage(method, Url);
        if (ApiKey() is { Length: > 0 } key)
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, key);
        return request;
    }

    public async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Head);
        if (last?.ETag is { Length: > 0 } etag && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
        if (last?.LastModified is { } modified) request.Headers.IfModifiedSince = modified;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified) return null;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException($"{Id}: Datafordeler refused the request; set Spark:ReferenceData:Sources:{Id}:ApiKey (user-secrets).");
        response.EnsureSuccessStatusCode();
        var newTag = response.Headers.ETag?.ToString();
        var newModified = response.Content.Headers.LastModified;
        if (last is not null && ((newTag is not null && newTag == last.ETag) || (newTag is null && newModified is not null && newModified == last.LastModified)))
            return null;
        return new SourceVersion(newModified?.UtcDateTime.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) ?? newTag?.Trim('"', 'W', '/') ?? "unversioned", Url, newTag, newModified);
    }

    public async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var zipPath = Path.Combine(workDirectory.FullName, Id + ".zip");
        string sha;
        using (var request = Request(HttpMethod.Get))
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(zipPath);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if ((total += read) > 8L * 1024 * 1024 * 1024)
                    throw new InvalidDataException($"{Id} sent more than the 8 GB cap.");
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            sha = "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        // The credit follows the version's own date (its Last-Modified), never the clock (K10).
        DateOnly? versionDate = version.LastModified is { } modified ? DateOnly.FromDateTime(modified.UtcDateTime) : null;
        var files = RefZip.Extract(zipPath, JsonEntry, workDirectory, 16L * 1024 * 1024 * 1024);
        // The URL recorded with the input carries no key (the key travels in a header).
        return new SourceInput
        {
            SourceId = Id, Version = version.Version, Url = Url, Sha256 = sha, Files = files,
            Attribution = DenmarkLicences.For(Dataset, versionDate).Attribution,
        };
    }

    private static readonly Regex JsonEntry = new(@"(?i)\.json$", RegexOptions.CultureInvariant);

    public SourceInput Fixture() => new()
    {
        SourceId = Id,
        Version = "fixture",
        Files = [.. EmbeddedSourceFiles.Under(typeof(DatafordelerSource).Assembly, "Fixtures/").Where(f => f.Name == FixtureName)],
        Attribution = DenmarkLicences.For(Dataset, FixtureDate).Attribution,
    };
}

/// <summary>DAR (<c>dk-dar</c>): named roads, their municipality parts (kommunekode + vejkode), postcodes and the road ↔ postcode relation.</summary>
internal sealed class DenmarkDarSource : DatafordelerSource
{
    public const string SourceId = "dk-dar";
    public override string Id => SourceId;
    protected override string Dataset => "DAR";
    protected override string DownloadQuery => "Register=DAR&type=current&format=JSON";
    protected override string FixtureName => "DAR_Totaludtraek.docderived.json";
    protected override DateOnly FixtureDate => new(2026, 10, 19);
    public override string DefaultSchedule => "0 0 5 * * 0";
}

/// <summary>DAGI (<c>dk-dagi</c>): regions and municipalities (published on Sundays).</summary>
internal sealed class DenmarkDagiSource : DatafordelerSource
{
    public const string SourceId = "dk-dagi";
    public override string Id => SourceId;
    protected override string Dataset => "DAGI";
    protected override string DownloadQuery => "Register=DAGI&type=current&format=JSON";
    protected override string FixtureName => "DAGI_Totaludtraek.docderived.json";
    protected override DateOnly FixtureDate => new(2026, 10, 19);
    public override string DefaultSchedule => "0 0 6 * * 1";
}
