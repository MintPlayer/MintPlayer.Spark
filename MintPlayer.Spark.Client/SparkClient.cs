using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Client;

/// <summary>
/// Typed .NET client for a Spark backend's <b>core</b> surface: PersistentObject CRUD,
/// queries, custom actions, and metadata endpoints. Handles CSRF round-tripping (warmup GET
/// + <c>X-XSRF-TOKEN</c> header on mutating requests), cookie-jar management, and
/// status-to-exception translation so callers work in terms of <see cref="PersistentObject"/>
/// and <see cref="QueryResult"/> instead of hand-building JSON bodies.
///
/// <para>
/// The client is intentionally framework-agnostic — Spark's Authentication package ships as
/// a separate nuget, and so do its client-side extension methods
/// (<c>MintPlayer.Spark.Client.Authorization</c>). The <see cref="SendAsync"/> and
/// <see cref="InvalidateAntiforgery"/> primitives are exposed publicly so third-party
/// extensions can implement their own endpoint families without needing
/// <c>InternalsVisibleTo</c>.
/// </para>
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

    // Cookie jar + CSRF token. TestServer's HttpClient doesn't auto-manage cookies, so we
    // track them ourselves — this also lets real-HTTP mode work identically without flipping
    // a CookieContainer on/off. "Primed" state is derived from _xsrfToken being non-null,
    // which UpdateCookiesFromResponse sets from any Set-Cookie it sees.
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);
    private string? _xsrfToken;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SparkClient(string baseUrl)
        : this(new HttpClient { BaseAddress = new Uri(baseUrl) }, ownsClient: true)
    {
    }

    public SparkClient(HttpClient httpClient, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _ownsClient = ownsClient;
    }

    // --------------------------------------------------------------------------------
    // Public low-level primitives — the extensibility surface for other packages.
    // --------------------------------------------------------------------------------

    /// <summary>
    /// Low-level send primitive. Attaches the accumulated cookies; if
    /// <paramref name="requiresAntiforgery"/> is true, primes the XSRF token (via a warmup
    /// GET if needed) and adds the <c>X-XSRF-TOKEN</c> header. Updates the internal cookie
    /// jar from any <c>Set-Cookie</c> response headers. Does <b>not</b> throw on non-success
    /// statuses — the caller decides what to do with <c>response.StatusCode</c>. Caller owns
    /// (and must dispose) the returned response.
    ///
    /// <para>This is the extensibility seam third-party packages hang new endpoint methods
    /// off of, in place of adding them to the core client.</para>
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        HttpContent? content = null,
        bool requiresAntiforgery = false,
        CancellationToken cancellationToken = default)
    {
        if (requiresAntiforgery)
            await EnsureAntiforgeryAsync(cancellationToken);

        var request = new HttpRequestMessage(method, url);
        var cookieHeader = BuildCookieHeader();
        if (cookieHeader is not null)
            request.Headers.Add("Cookie", cookieHeader);
        if (requiresAntiforgery)
        {
            // EnsureAntiforgeryAsync guarantees _xsrfToken is non-null when it returns.
            request.Headers.Add("X-XSRF-TOKEN", _xsrfToken!);
        }
        if (content is not null)
            request.Content = content;

        var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateCookiesFromResponse(response);
        return response;
    }

    /// <summary>
    /// Drops any cached antiforgery state (the XSRF token + its cookie) so the next call
    /// through <see cref="SendAsync"/> with <c>requiresAntiforgery: true</c> re-primes from
    /// a fresh warmup. Needed after login: a pre-auth XSRF token is bound to the anonymous
    /// principal and is no longer valid once the session cookie changes identity.
    /// </summary>
    public void InvalidateAntiforgery()
    {
        _xsrfToken = null;
        _cookies.Remove("XSRF-TOKEN");
    }

    // --------------------------------------------------------------------------------
    // PersistentObject endpoints
    // --------------------------------------------------------------------------------

    /// <summary>Returns the PersistentObject with its <see cref="PersistentObject.Etag"/> populated, or null on 404.</summary>
    public Task<PersistentObject?> GetPersistentObjectAsync(Guid objectTypeId, string id, CancellationToken cancellationToken = default)
        => GetPersistentObjectCoreAsync(objectTypeId.ToString(), id, cancellationToken);

    /// <summary>
    /// Alias-based overload. <paramref name="aliasOrName"/> is resolved server-side to an
    /// entity type, so callers that only know the type by name (e.g. <c>"Person"</c>) don't
    /// need to look up its Guid first. Returns null on 404 (entity missing or row-level
    /// denied — the endpoint conflates these per security audit M-3).
    /// </summary>
    public Task<PersistentObject?> GetPersistentObjectAsync(string aliasOrName, string id, CancellationToken cancellationToken = default)
        => GetPersistentObjectCoreAsync(Uri.EscapeDataString(aliasOrName), id, cancellationToken);

    private async Task<PersistentObject?> GetPersistentObjectCoreAsync(string typeSegment, string id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/spark/po/{typeSegment}/{Uri.EscapeDataString(id)}", cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PersistentObject>(JsonOptions, cancellationToken);
    }

    /// <summary>
    /// Creates a new PersistentObject. The instance's <see cref="PersistentObject.Id"/> must be
    /// null on input; the server assigns it and returns the populated object.
    /// </summary>
    /// <remarks>
    /// The Create endpoint returns the new <c>ClientInstructionEnvelope</c> wire shape
    /// (<c>{ result, instructions }</c>); this method unwraps the envelope and returns just the
    /// <see cref="PersistentObject"/>. Any client instructions emitted by server-side action code
    /// (notify / navigate / refresh / disableAction) are currently dropped by this SDK — see
    /// docs/PRD-ClientInstructions.md.
    /// </remarks>
    public async Task<PersistentObject> CreatePersistentObjectAsync(PersistentObject obj, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var target = $"/spark/po/{obj.Name}";
        var content = JsonContent.Create(new { persistentObject = obj }, options: JsonOptions);
        using var response = await SendAsync(HttpMethod.Post, target, content, requiresAntiforgery: true, cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        return await ReadEnvelopeResultAsync<PersistentObject>(response, cancellationToken)
            ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty response body.");
    }

    /// <summary>
    /// Updates an existing PersistentObject. The instance's <see cref="PersistentObject.Id"/> is
    /// required; <see cref="PersistentObject.Etag"/> is echoed back to the server for the
    /// optimistic-concurrency check — a stale etag surfaces as <see cref="SparkClientException"/>
    /// with <c>StatusCode = HttpStatusCode.Conflict</c>.
    /// </summary>
    public Task<PersistentObject> UpdatePersistentObjectAsync(PersistentObject obj, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (string.IsNullOrEmpty(obj.Id))
            throw new ArgumentException("PersistentObject must have an Id for update.", nameof(obj));
        var target = $"/spark/po/{obj.ObjectTypeId}/{Uri.EscapeDataString(obj.Id)}";
        return SendPersistentObjectAsync(HttpMethod.Put, target, obj, cancellationToken);
    }

    public async Task DeletePersistentObjectAsync(Guid objectTypeId, string id, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Delete,
            $"/spark/po/{objectTypeId}/{Uri.EscapeDataString(id)}",
            requiresAntiforgery: true,
            cancellationToken: cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
    }

    /// <summary>
    /// Returns every PersistentObject of the given type that the caller can see. Row-level
    /// authorization applies — denied rows are filtered out server-side, so a successful
    /// response is already the caller's visible set.
    /// </summary>
    public Task<IReadOnlyList<PersistentObject>> ListPersistentObjectsAsync(Guid objectTypeId, CancellationToken cancellationToken = default)
        => ListPersistentObjectsCoreAsync(objectTypeId.ToString(), cancellationToken);

    /// <summary>Alias-based overload for <see cref="ListPersistentObjectsAsync(Guid,CancellationToken)"/>.</summary>
    public Task<IReadOnlyList<PersistentObject>> ListPersistentObjectsAsync(string aliasOrName, CancellationToken cancellationToken = default)
        => ListPersistentObjectsCoreAsync(Uri.EscapeDataString(aliasOrName), cancellationToken);

    private async Task<IReadOnlyList<PersistentObject>> ListPersistentObjectsCoreAsync(string typeSegment, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/spark/po/{typeSegment}", cancellationToken: cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        var list = await response.Content.ReadFromJsonAsync<PersistentObject[]>(JsonOptions, cancellationToken);
        return list ?? Array.Empty<PersistentObject>();
    }

    // --------------------------------------------------------------------------------
    // Query endpoints
    // --------------------------------------------------------------------------------

    public Task<QueryResult> ExecuteQueryAsync(
        Guid queryId,
        int skip = 0,
        int take = 50,
        string? search = null,
        string? parentId = null,
        string? parentType = null,
        string? sortColumns = null,
        CancellationToken cancellationToken = default)
        => ExecuteQueryCoreAsync(queryId.ToString(), skip, take, search, parentId, parentType, sortColumns, cancellationToken);

    /// <summary>Executes a query by its alias (e.g. <c>"allpeople"</c>) instead of by Guid.</summary>
    public Task<QueryResult> ExecuteQueryAsync(
        string queryAlias,
        int skip = 0,
        int take = 50,
        string? search = null,
        string? parentId = null,
        string? parentType = null,
        string? sortColumns = null,
        CancellationToken cancellationToken = default)
        => ExecuteQueryCoreAsync(Uri.EscapeDataString(queryAlias), skip, take, search, parentId, parentType, sortColumns, cancellationToken);

    private async Task<QueryResult> ExecuteQueryCoreAsync(
        string idSegment, int skip, int take, string? search, string? parentId, string? parentType, string? sortColumns,
        CancellationToken cancellationToken)
    {
        var url = BuildQueryUrl(idSegment, skip, take, search, parentId, parentType, sortColumns);
        using var response = await SendAsync(HttpMethod.Get, url, cancellationToken: cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<QueryResult>(JsonOptions, cancellationToken)
            ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty query response body.");
    }

    private static string BuildQueryUrl(string idSegment, int skip, int take, string? search, string? parentId, string? parentType, string? sortColumns)
    {
        var qs = new List<string> { $"skip={skip}", $"take={take}" };
        if (!string.IsNullOrEmpty(search)) qs.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrEmpty(parentId)) qs.Add($"parentId={Uri.EscapeDataString(parentId)}");
        if (!string.IsNullOrEmpty(parentType)) qs.Add($"parentType={Uri.EscapeDataString(parentType)}");
        if (!string.IsNullOrEmpty(sortColumns)) qs.Add($"sortColumns={Uri.EscapeDataString(sortColumns)}");
        return $"/spark/queries/{idSegment}/execute?{string.Join('&', qs)}";
    }

    /// <summary>
    /// Returns the full definition of a single query (name, source, sort columns, etc.), or
    /// <c>null</c> on 404. Counterpart to the list variant — useful when a caller already knows
    /// the query id and just needs its shape.
    /// </summary>
    public Task<SparkQuery?> GetQueryAsync(Guid queryId, CancellationToken cancellationToken = default)
        => GetQueryCoreAsync(queryId.ToString(), cancellationToken);

    /// <summary>Alias-based overload for <see cref="GetQueryAsync(Guid,CancellationToken)"/>.</summary>
    public Task<SparkQuery?> GetQueryAsync(string alias, CancellationToken cancellationToken = default)
        => GetQueryCoreAsync(Uri.EscapeDataString(alias), cancellationToken);

    private async Task<SparkQuery?> GetQueryCoreAsync(string idSegment, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/spark/queries/{idSegment}", cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SparkQuery>(JsonOptions, cancellationToken);
    }

    /// <summary>
    /// Returns every query the caller is allowed to see. Row-level visibility for queries is
    /// enforced server-side — the result is already the caller's filtered set.
    /// </summary>
    public async Task<IReadOnlyList<SparkQuery>> ListQueriesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/spark/queries", cancellationToken: cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        var list = await response.Content.ReadFromJsonAsync<SparkQuery[]>(JsonOptions, cancellationToken);
        return list ?? Array.Empty<SparkQuery>();
    }

    // --------------------------------------------------------------------------------
    // Metadata + permissions endpoints
    // --------------------------------------------------------------------------------

    /// <summary>
    /// Returns every entity type definition the caller is allowed to see. The server applies
    /// the <c>Query</c> permission check per entity type before including it, so an anonymous
    /// caller gets only the subset exposed to Everyone.
    /// </summary>
    public async Task<IReadOnlyList<EntityTypeDefinition>> ListEntityTypesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/spark/types", cancellationToken: cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        var list = await response.Content.ReadFromJsonAsync<EntityTypeDefinition[]>(JsonOptions, cancellationToken);
        return list ?? Array.Empty<EntityTypeDefinition>();
    }

    /// <summary>
    /// Returns the alias maps (entity types + queries). Aliases are filtered server-side to
    /// the set the caller has <c>Query</c> rights on — absent entries don't reveal existence.
    /// </summary>
    public async Task<SparkAliases> ListAliasesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/spark/aliases", cancellationToken: cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SparkAliases>(JsonOptions, cancellationToken)
            ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty /spark/aliases response.");
    }

    /// <summary>
    /// Returns the permission flags for a single entity type, addressed by Guid id, name, or
    /// alias. Null on 404 (entity type unknown). Anonymous callers still get a response with
    /// all-false flags for types they can't access — consistent with the Angular SPA's need
    /// to render "view-only" UI without throwing.
    /// </summary>
    public async Task<SparkPermissions?> GetPermissionsAsync(string entityTypeIdOrNameOrAlias, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/spark/permissions/{Uri.EscapeDataString(entityTypeIdOrNameOrAlias)}", cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SparkPermissions>(JsonOptions, cancellationToken);
    }

    // --------------------------------------------------------------------------------
    // Action endpoints
    // --------------------------------------------------------------------------------

    /// <summary>
    /// POSTs to <c>/spark/actions/{objectTypeId}/{actionName}</c>. Returns a
    /// <see cref="SparkActionResult"/> that distinguishes the server's in-protocol responses:
    /// empty-200 (action completed), 449 (retry-action — server is asking the caller a
    /// question). Actual failures (401/403/404/500) throw <see cref="SparkClientException"/>.
    /// </summary>
    public async Task<SparkActionResult> ExecuteActionAsync(
        Guid objectTypeId,
        string actionName,
        PersistentObject? parent = null,
        IReadOnlyList<PersistentObject>? selectedItems = null,
        CancellationToken cancellationToken = default)
    {
        var content = JsonContent.Create(new { parent, selectedItems }, options: JsonOptions);
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/spark/actions/{objectTypeId}/{Uri.EscapeDataString(actionName)}",
            content,
            requiresAntiforgery: true,
            cancellationToken);

        // 449 (Retry With) is in-protocol; translate to a populated SparkActionResult rather
        // than throwing, because it's not an error — the server is asking a question.
        if ((int)response.StatusCode == 449)
        {
            var retry = await response.Content.ReadFromJsonAsync<RetryActionPayload>(JsonOptions, cancellationToken)
                ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty retry-action response body.");
            return SparkActionResult.ForRetry(retry);
        }

        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        return SparkActionResult.ForSuccess((int)response.StatusCode);
    }

    // --------------------------------------------------------------------------------
    // CSRF / internals
    // --------------------------------------------------------------------------------

    private async Task<PersistentObject> SendPersistentObjectAsync(HttpMethod method, string url, PersistentObject obj, CancellationToken cancellationToken)
    {
        var content = JsonContent.Create(new { persistentObject = obj }, options: JsonOptions);
        using var response = await SendAsync(method, url, content, requiresAntiforgery: true, cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PersistentObject>(JsonOptions, cancellationToken)
            ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty response body.");
    }

    /// <summary>
    /// Reads a <c>{ result, instructions }</c> envelope (per PRD-ClientInstructions) and
    /// extracts the typed <c>result</c> field. Returns <c>default</c> when the result is
    /// null / absent. Instructions are currently discarded — re-expose them via a richer
    /// return type when the SDK starts surfacing client-side side-effects.
    /// </summary>
    private async Task<T?> ReadEnvelopeResultAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(JsonOptions, cancellationToken);
        if (doc is null) return default;
        if (!doc.RootElement.TryGetProperty("result", out var resultEl)) return default;
        if (resultEl.ValueKind == JsonValueKind.Null) return default;
        return resultEl.Deserialize<T>(JsonOptions);
    }

    /// <summary>
    /// Ensures <see cref="_xsrfToken"/> is populated. If the server previously returned one
    /// via Set-Cookie (on any read), the token is already cached. Otherwise, fires a warmup
    /// GET to <c>/spark/po/__warmup__</c> which always mints the antiforgery cookie pair.
    /// </summary>
    private async Task EnsureAntiforgeryAsync(CancellationToken cancellationToken)
    {
        if (_xsrfToken is not null) return;

        var warmupRequest = new HttpRequestMessage(HttpMethod.Get, "/spark/po/__warmup__");
        var cookieHeader = BuildCookieHeader();
        if (cookieHeader is not null)
            warmupRequest.Headers.Add("Cookie", cookieHeader);
        using var response = await _httpClient.SendAsync(warmupRequest, cancellationToken);
        UpdateCookiesFromResponse(response);

        if (_xsrfToken is null)
            throw new SparkClientException(response.StatusCode, responseBody: null,
                "Warmup did not yield an XSRF-TOKEN cookie — is this endpoint a Spark backend?");
    }

    private string? BuildCookieHeader()
        => _cookies.Count == 0
            ? null
            : string.Join("; ", _cookies.Select(kv => $"{kv.Key}={kv.Value}"));

    private void UpdateCookiesFromResponse(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies)) return;
        foreach (var raw in setCookies)
        {
            var nameValue = raw.Split(';', 2)[0];
            var eq = nameValue.IndexOf('=');
            if (eq < 0) continue;
            var name = nameValue[..eq];
            var value = nameValue[(eq + 1)..];

            // A Set-Cookie with an empty value (+ past Expires/Max-Age=0) is a deletion.
            if (string.IsNullOrEmpty(value))
            {
                _cookies.Remove(name);
            }
            else
            {
                _cookies[name] = value;
                if (name == "XSRF-TOKEN") _xsrfToken = Uri.UnescapeDataString(value);
            }
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}
