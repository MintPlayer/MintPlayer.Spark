using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace MintPlayer.Spark.ReferenceData.Sources;

/// <summary>
/// Download helpers for adapters (PRD §4.5 steps 2 and 4): a conditional HEAD/GET for the change check and a capped
/// streaming download to a temporary file. The named client behind them refuses hosts no source declared.
/// </summary>
public static class RefDownloads
{
    /// <summary>The named <see cref="HttpClient"/> the import pipeline hands to sources.</summary>
    public const string HttpClientName = "MintPlayer.Spark.ReferenceData";

    /// <summary>
    /// The conditional change check: <c>If-None-Match</c> / <c>If-Modified-Since</c> against the last state.
    /// <see langword="null"/> when the server answers 304 or the validators did not move.
    /// </summary>
    public static async Task<SourceVersion?> CheckConditionalAsync(HttpClient http, string url, SourceState? last, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        if (last?.ETag is { Length: > 0 } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
            request.Headers.IfNoneMatch.Add(tag);
        if (last?.LastModified is { } modified)
            request.Headers.IfModifiedSince = modified;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return null;
        response.EnsureSuccessStatusCode();
        var newTag = response.Headers.ETag?.ToString();
        var newModified = response.Content.Headers.LastModified;
        if (last is not null && newTag is not null && newTag == last.ETag)
            return null;
        if (last is not null && newTag is null && newModified is not null && newModified == last.LastModified)
            return null;
        var version = newTag?.Trim('"', 'W', '/') ?? newModified?.UtcDateTime.ToString("yyyyMMddHHmmss") ?? "unversioned";
        return new SourceVersion(version, url, newTag, newModified);
    }

    /// <summary>Streams <paramref name="url"/> to <paramref name="path"/>, refusing more than <paramref name="maxBytes"/>; returns the SHA-256.</summary>
    public static async Task<string> DownloadAsync(HttpClient http, string url, string path, long maxBytes, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 0 and var length && length > maxBytes)
            throw new InvalidDataException($"{url} announces {length} bytes, more than the {maxBytes}-byte cap.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidDataException($"{url} sent more than the {maxBytes}-byte cap.");
            sha.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return "sha256:" + Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}

/// <summary>Refuses a request to a host no registered source declared (§4.5 step 4).</summary>
internal sealed class RefDataHostAllowListHandler(IEnumerable<IReferenceDataSource> sources) : DelegatingHandler
{
    private readonly HashSet<string> hosts = new(sources.SelectMany(s => s.DownloadHosts), StringComparer.OrdinalIgnoreCase);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { Scheme: "https" or "http" } uri || !hosts.Contains(uri.Host))
            throw new HttpRequestException($"Reference data may not download from '{request.RequestUri?.Host}': no registered source declares that host.");
        return base.SendAsync(request, cancellationToken);
    }
}
