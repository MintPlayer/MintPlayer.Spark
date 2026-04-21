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

    // Cookie jar + CSRF token. TestServer's HttpClient doesn't auto-manage cookies, so we
    // track them ourselves — this also lets real-HTTP mode work identically without flipping
    // a CookieContainer on/off.
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);
    private string? _xsrfToken;
    private bool _antiforgeryPrimed;

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
        using var request = BuildRequest(HttpMethod.Get, $"/spark/po/{typeSegment}/{Uri.EscapeDataString(id)}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateCookiesFromResponse(response);
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
        using var request = BuildRequest(HttpMethod.Delete, $"/spark/po/{objectTypeId}/{Uri.EscapeDataString(id)}", attachAntiforgery: true);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateCookiesFromResponse(response);
        await ThrowIfNotSuccessAsync(response, cancellationToken);
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
        using var request = BuildRequest(HttpMethod.Get, $"/spark/po/{typeSegment}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateCookiesFromResponse(response);
        await ThrowIfNotSuccessAsync(response, cancellationToken);
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
        using var request = BuildRequest(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateCookiesFromResponse(response);
        await ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<QueryResult>(JsonOptions, cancellationToken)
            ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty query response body.");
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
        using var request = BuildRequest(HttpMethod.Get, $"/spark/queries/{idSegment}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateCookiesFromResponse(response);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SparkQuery>(JsonOptions, cancellationToken);
    }

    /// <summary>
    /// Returns every query the caller is allowed to see. Row-level visibility for queries is
    /// enforced server-side — the result is already the caller's filtered set.
    /// </summary>
    public async Task<IReadOnlyList<SparkQuery>> ListQueriesAsync(CancellationToken cancellationToken = default)
    {
        using var request = BuildRequest(HttpMethod.Get, "/spark/queries");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateCookiesFromResponse(response);
        await ThrowIfNotSuccessAsync(response, cancellationToken);
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
        using var request = BuildRequest(HttpMethod.Get, "/spark/types");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateCookiesFromResponse(response);
        await ThrowIfNotSuccessAsync(response, cancellationToken);
        var list = await response.Content.ReadFromJsonAsync<EntityTypeDefinition[]>(JsonOptions, cancellationToken);
        return list ?? Array.Empty<EntityTypeDefinition>();
    }

    /// <summary>
    /// Returns the alias maps (entity types + queries). Aliases are filtered server-side to
    /// the set the caller has <c>Query</c> rights on — absent entries don't reveal existence.
    /// </summary>
    public async Task<SparkAliases> ListAliasesAsync(CancellationToken cancellationToken = default)
    {
        using var request = BuildRequest(HttpMethod.Get, "/spark/aliases");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateCookiesFromResponse(response);
        await ThrowIfNotSuccessAsync(response, cancellationToken);
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
        using var request = BuildRequest(HttpMethod.Get, $"/spark/permissions/{Uri.EscapeDataString(entityTypeIdOrNameOrAlias)}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateCookiesFromResponse(response);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SparkPermissions>(JsonOptions, cancellationToken);
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
        await EnsureAntiforgeryAsync(cancellationToken);
        using var request = BuildRequest(HttpMethod.Post, $"/spark/actions/{objectTypeId}/{Uri.EscapeDataString(actionName)}", attachAntiforgery: true);
        request.Content = JsonContent.Create(new { parent, selectedItems }, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateCookiesFromResponse(response);

        // 449 (Retry With) is in-protocol; translate to a populated SparkActionResult rather
        // than throwing, because it's not an error — the server is asking a question.
        if ((int)response.StatusCode == 449)
        {
            var retry = await response.Content.ReadFromJsonAsync<RetryActionPayload>(JsonOptions, cancellationToken)
                ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty retry-action response body.");
            return SparkActionResult.ForRetry(retry);
        }

        await ThrowIfNotSuccessAsync(response, cancellationToken);
        return SparkActionResult.ForSuccess((int)response.StatusCode);
    }

    // --------------------------------------------------------------------------------
    // Auth endpoints
    // --------------------------------------------------------------------------------

    /// <summary>
    /// Signs in via <c>POST /spark/auth/login?useCookies=true</c>. The identity API endpoint
    /// is outside Spark's antiforgery surface, so no CSRF header is sent. After login, the
    /// client re-primes its XSRF token via <see cref="GetCurrentUserAsync"/> — the token
    /// is bound to the authenticated principal and the pre-login one is no longer valid.
    /// </summary>
    public async Task LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        using var request = BuildRequest(HttpMethod.Post, "/spark/auth/login?useCookies=true");
        request.Content = JsonContent.Create(new { email, password }, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateCookiesFromResponse(response);
        await ThrowIfNotSuccessAsync(response, cancellationToken);

        // Post-login: the XSRF token that was minted pre-auth is no longer valid for mutating
        // calls. Drop it and re-prime by hitting /me — the warmup logic will pick up the
        // fresh token from the response.
        _xsrfToken = null;
        _cookies.Remove("XSRF-TOKEN");
        _antiforgeryPrimed = false;
        await GetCurrentUserAsync(cancellationToken);
    }

    /// <summary>
    /// Registers a new user via <c>POST /spark/auth/register</c>. Outside Spark's antiforgery
    /// surface. Does not automatically sign the user in — call <see cref="LoginAsync"/> if that's
    /// the intent.
    /// </summary>
    public async Task RegisterAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        using var request = BuildRequest(HttpMethod.Post, "/spark/auth/register");
        request.Content = JsonContent.Create(new { email, password }, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateCookiesFromResponse(response);
        await ThrowIfNotSuccessAsync(response, cancellationToken);
    }

    /// <summary>
    /// Signs out via <c>POST /spark/auth/logout</c>. The logout endpoint is inside Spark's
    /// antiforgery surface, so the client attaches the X-XSRF-TOKEN header.
    /// </summary>
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAntiforgeryAsync(cancellationToken);
        using var request = BuildRequest(HttpMethod.Post, "/spark/auth/logout", attachAntiforgery: true);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateCookiesFromResponse(response);
        await ThrowIfNotSuccessAsync(response, cancellationToken);

        // Server should have cleared the auth cookie via Set-Cookie on response; in addition,
        // drop any cached principal-bound XSRF token so the next mutating call re-primes.
        _xsrfToken = null;
        _cookies.Remove("XSRF-TOKEN");
        _antiforgeryPrimed = false;
    }

    /// <summary>
    /// Returns <c>GET /spark/auth/me</c> — the lightweight "who am I" payload. Also serves as
    /// the warmup that mints the XSRF token bound to whatever principal the current cookies
    /// represent (authenticated or anonymous).
    /// </summary>
    public async Task<SparkUserInfo> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        using var request = BuildRequest(HttpMethod.Get, "/spark/auth/me");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateCookiesFromResponse(response);
        await ThrowIfNotSuccessAsync(response, cancellationToken);

        // /me may or may not set the XSRF cookie depending on middleware order, but when it
        // does, UpdateCookiesFromResponse already captured _xsrfToken — mark us primed.
        if (_xsrfToken is not null) _antiforgeryPrimed = true;

        return await response.Content.ReadFromJsonAsync<SparkUserInfo>(JsonOptions, cancellationToken)
            ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty /me response.");
    }

    // --------------------------------------------------------------------------------
    // CSRF / request assembly
    // --------------------------------------------------------------------------------

    private async Task<PersistentObject> SendPersistentObjectAsync(HttpMethod method, string url, PersistentObject obj, CancellationToken cancellationToken)
    {
        await EnsureAntiforgeryAsync(cancellationToken);
        using var request = BuildRequest(method, url, attachAntiforgery: true);
        request.Content = JsonContent.Create(new { persistentObject = obj }, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateCookiesFromResponse(response);
        await ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PersistentObject>(JsonOptions, cancellationToken)
            ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty response body.");
    }

    /// <summary>
    /// Lazily primes <see cref="_xsrfToken"/> by hitting the framework's antiforgery warmup
    /// endpoint. Only called before the first mutating request — reads don't need CSRF.
    /// </summary>
    private async Task EnsureAntiforgeryAsync(CancellationToken cancellationToken)
    {
        if (_antiforgeryPrimed && _xsrfToken is not null) return;

        using var request = BuildRequest(HttpMethod.Get, "/spark/po/__warmup__");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateCookiesFromResponse(response);

        if (_xsrfToken is null)
            throw new SparkClientException(response.StatusCode, responseBody: null,
                "Warmup did not yield an XSRF-TOKEN cookie — is this endpoint a Spark backend?");

        _antiforgeryPrimed = true;
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string url, bool attachAntiforgery = false)
    {
        var request = new HttpRequestMessage(method, url);
        var cookieHeader = BuildCookieHeader();
        if (cookieHeader is not null)
            request.Headers.Add("Cookie", cookieHeader);
        if (attachAntiforgery)
        {
            if (_xsrfToken is null)
                throw new InvalidOperationException("Antiforgery token not initialized — call EnsureAntiforgeryAsync first.");
            request.Headers.Add("X-XSRF-TOKEN", _xsrfToken);
        }
        return request;
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

            // A Set-Cookie with an empty value + a past Expires/Max-Age=0 is a deletion.
            // Simple heuristic: treat an explicitly empty cookie value as deletion; real
            // deletion also carries Max-Age=0 but that's redundant signal for our needs.
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
