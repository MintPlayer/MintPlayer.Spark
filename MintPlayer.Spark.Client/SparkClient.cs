using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Client;

/// <summary>
/// Typed .NET client for a Spark backend. Handles CSRF round-tripping (warmup GET +
/// <c>X-XSRF-TOKEN</c> header on mutating requests), serialization, and status-to-exception
/// translation so callers can work in terms of <see cref="PersistentObject"/> and
/// <see cref="QueryResult"/> instead of hand-building JSON bodies and URL strings.
///
/// Two construction modes:
/// <list type="bullet">
///   <item><description><c>new SparkClient(baseUrl)</c> — real HTTP use; owns an internal <see cref="HttpClient"/>.</description></item>
///   <item><description><c>new SparkClient(httpClient)</c> — wrap an existing client (e.g. one returned
///     by <c>SparkEndpointFactory.CreateClient()</c> for <c>TestServer</c>-backed tests).</description></item>
/// </list>
/// </summary>
public class SparkClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private string? _cookieHeader;
    private string? _xsrfToken;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SparkClient(string baseUrl)
        : this(new HttpClient { BaseAddress = new Uri(baseUrl) }, ownsClient: true)
    {
    }

    public SparkClient(HttpClient httpClient, bool ownsClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsClient = ownsClient;
    }

    // --------------------------------------------------------------------------------
    // PersistentObject endpoints
    // --------------------------------------------------------------------------------

    /// <summary>Returns the PersistentObject with its <see cref="PersistentObject.Etag"/> populated, or null on 404.</summary>
    public async Task<PersistentObject?> GetPersistentObjectAsync(Guid objectTypeId, string id, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"/spark/po/{objectTypeId}/{Uri.EscapeDataString(id)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PersistentObject>(JsonOptions, cancellationToken);
    }

    /// <summary>
    /// Creates a new PersistentObject. The instance's <see cref="PersistentObject.Id"/> must be
    /// null on input; the server assigns it and returns the populated object.
    /// </summary>
    public Task<PersistentObject> CreatePersistentObjectAsync(PersistentObject obj, CancellationToken cancellationToken = default)
    {
        if (obj is null) throw new ArgumentNullException(nameof(obj));
        var target = $"/spark/po/{obj.Name}";
        return SendPersistentObjectAsync(HttpMethod.Post, target, obj, cancellationToken);
    }

    /// <summary>
    /// Updates an existing PersistentObject. The instance's <see cref="PersistentObject.Id"/> is
    /// required; <see cref="PersistentObject.Etag"/> is echoed back to the server for the
    /// optimistic-concurrency check — a stale etag surfaces as <see cref="SparkClientException"/>
    /// with <c>StatusCode = HttpStatusCode.Conflict</c>.
    /// </summary>
    public Task<PersistentObject> UpdatePersistentObjectAsync(PersistentObject obj, CancellationToken cancellationToken = default)
    {
        if (obj is null) throw new ArgumentNullException(nameof(obj));
        if (string.IsNullOrEmpty(obj.Id))
            throw new ArgumentException("PersistentObject must have an Id for update.", nameof(obj));
        var target = $"/spark/po/{obj.ObjectTypeId}/{Uri.EscapeDataString(obj.Id)}";
        return SendPersistentObjectAsync(HttpMethod.Put, target, obj, cancellationToken);
    }

    public async Task DeletePersistentObjectAsync(Guid objectTypeId, string id, CancellationToken cancellationToken = default)
    {
        await EnsureAntiforgeryAsync(cancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/spark/po/{objectTypeId}/{Uri.EscapeDataString(id)}");
        AttachAntiforgery(request);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        await ThrowIfNotSuccessAsync(response, cancellationToken);
    }

    // --------------------------------------------------------------------------------
    // Query endpoints
    // --------------------------------------------------------------------------------

    public async Task<QueryResult> ExecuteQueryAsync(
        Guid queryId,
        int skip = 0,
        int take = 50,
        string? search = null,
        string? parentId = null,
        string? parentType = null,
        CancellationToken cancellationToken = default)
    {
        var url = BuildQueryUrl(queryId.ToString(), skip, take, search, parentId, parentType);
        return await ExecuteQueryAsyncCore(url, cancellationToken);
    }

    /// <summary>Executes a query by its alias (e.g. <c>"allpeople"</c>) instead of by Guid.</summary>
    public async Task<QueryResult> ExecuteQueryAsync(
        string queryAlias,
        int skip = 0,
        int take = 50,
        string? search = null,
        string? parentId = null,
        string? parentType = null,
        CancellationToken cancellationToken = default)
    {
        var url = BuildQueryUrl(Uri.EscapeDataString(queryAlias), skip, take, search, parentId, parentType);
        return await ExecuteQueryAsyncCore(url, cancellationToken);
    }

    private async Task<QueryResult> ExecuteQueryAsyncCore(string url, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(url, cancellationToken);
        await ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<QueryResult>(JsonOptions, cancellationToken)
            ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty query response body.");
    }

    private static string BuildQueryUrl(string idSegment, int skip, int take, string? search, string? parentId, string? parentType)
    {
        var qs = new List<string> { $"skip={skip}", $"take={take}" };
        if (!string.IsNullOrEmpty(search)) qs.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrEmpty(parentId)) qs.Add($"parentId={Uri.EscapeDataString(parentId)}");
        if (!string.IsNullOrEmpty(parentType)) qs.Add($"parentType={Uri.EscapeDataString(parentType)}");
        return $"/spark/queries/{idSegment}/execute?{string.Join('&', qs)}";
    }

    // --------------------------------------------------------------------------------
    // CSRF / internals
    // --------------------------------------------------------------------------------

    private async Task<PersistentObject> SendPersistentObjectAsync(HttpMethod method, string url, PersistentObject obj, CancellationToken cancellationToken)
    {
        await EnsureAntiforgeryAsync(cancellationToken);
        var request = new HttpRequestMessage(method, url)
        {
            Content = JsonContent.Create(new { persistentObject = obj }, options: JsonOptions),
        };
        AttachAntiforgery(request);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        await ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PersistentObject>(JsonOptions, cancellationToken)
            ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty response body.");
    }

    /// <summary>
    /// Lazily primes <see cref="_cookieHeader"/> and <see cref="_xsrfToken"/> by hitting the
    /// framework's antiforgery warmup endpoint. Only called before the first mutating request —
    /// reads don't need CSRF.
    /// </summary>
    private async Task EnsureAntiforgeryAsync(CancellationToken cancellationToken)
    {
        if (_xsrfToken is not null) return;

        var response = await _httpClient.GetAsync("/spark/po/__warmup__", cancellationToken);
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            throw new SparkClientException(response.StatusCode, responseBody: null,
                "Warmup did not return any Set-Cookie headers — is this endpoint a Spark backend?");

        string? antiforgeryCookie = null;
        string? xsrfToken = null;
        foreach (var raw in setCookies)
        {
            var nameValue = raw.Split(';', 2)[0];
            var eq = nameValue.IndexOf('=');
            if (eq < 0) continue;
            var name = nameValue[..eq];
            var value = nameValue[(eq + 1)..];

            if (name.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal))
                antiforgeryCookie = nameValue;
            else if (name == "XSRF-TOKEN")
                xsrfToken = Uri.UnescapeDataString(value);
        }

        if (antiforgeryCookie is null || xsrfToken is null)
            throw new SparkClientException(response.StatusCode, responseBody: null,
                $"Warmup did not yield both antiforgery cookies. Got: '{string.Join(" | ", setCookies)}'");

        _cookieHeader = antiforgeryCookie + "; XSRF-TOKEN=" + Uri.EscapeDataString(xsrfToken);
        _xsrfToken = xsrfToken;
    }

    private void AttachAntiforgery(HttpRequestMessage request)
    {
        if (_cookieHeader is null || _xsrfToken is null)
            throw new InvalidOperationException("Antiforgery tokens not initialized — call EnsureAntiforgeryAsync first.");
        request.Headers.Add("Cookie", _cookieHeader);
        request.Headers.Add("X-XSRF-TOKEN", _xsrfToken);
    }

    private static async Task ThrowIfNotSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new SparkClientException(
            response.StatusCode,
            body,
            $"Spark request failed with {(int)response.StatusCode} {response.StatusCode}: {Truncate(body, 500)}");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}
