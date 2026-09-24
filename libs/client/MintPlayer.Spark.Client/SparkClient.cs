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
public partial class SparkClient : IDisposable
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

    /// <summary>
    /// Answers retry prompts for calls that do not pass their own <c>onRetry</c>. Null by default, so
    /// a prompt nobody expected surfaces as <see cref="SparkRetryRequiredException"/> rather than
    /// being silently answered.
    /// </summary>
    /// <remarks>
    /// Safe to set once and share: the handler is a function, and the conversation state it answers
    /// into lives in the request being retried. See <see cref="SparkRetryHandler"/>.
    /// </remarks>
    public SparkRetryHandler? RetryHandler { get; set; }

    /// <summary>
    /// Observes client operations for calls that do not pass their own <c>onOperation</c>. Null by
    /// default, in which case operations are parsed and discarded.
    /// </summary>
    /// <remarks>
    /// ⚠️ Operations are never <b>applied</b> automatically. This SDK keeps no registry of open
    /// objects the way the frontend does, so there is nothing for a <c>refreshAttribute</c> to land
    /// on until the caller says which object it holds — see
    /// <see cref="SparkClientOperations.Apply(PersistentObject, IEnumerable{SparkClientOperation})"/>.
    /// </remarks>
    public SparkOperationHandler? OperationHandler { get; set; }

    /// <summary>
    /// How many prompts one call will answer before giving up. Default 16.
    /// </summary>
    /// <remarks>
    /// ⚠️ This is a <b>termination</b> bound, not a capacity one. A hook that re-raises the same step
    /// regardless of the answer — the ordinary shape of a bug in a hook — would otherwise loop until
    /// the process died, with every round trip looking individually reasonable. Sixteen is far above
    /// any real conversation (Fleet's longest is two) and far below anything that hides a spin.
    /// </remarks>
    public int MaxRetryDepth { get; set; } = 16;

    /// <summary>
    /// The viewer's IANA timezone (e.g. <c>"Europe/Brussels"</c>), sent as <c>X-Spark-Timezone</c>.
    /// Unset by default.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The server falls back to UTC silently</b> when the header is absent, blank or names a
    /// zone it does not know — no error, no log. So a test asserting viewer-zone behaviour through
    /// this client is asserting the fallback until this is set, and it passes either way.
    /// </remarks>
    public string? TimeZoneId { get; set; }

    /// <summary>
    /// The value to send as <c>Accept-Language</c> (e.g. <c>"nl-BE,nl;q=0.9"</c>). Unset by default.
    /// </summary>
    /// <remarks>
    /// ⚠️ Same silent fallback as <see cref="TimeZoneId"/>: an absent or unsupported language
    /// resolves to the application's configured default.
    /// </remarks>
    public string? AcceptLanguage { get; set; }

    public SparkClient(string baseUrl)
        : this(BuildDefaultHttpClient(new Uri(baseUrl)), ownsClient: true)
    {
    }

    public SparkClient(HttpClient httpClient, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _ownsClient = ownsClient;
    }

    /// <summary>
    /// Builds the HttpClient used by the convenience ctor:
    /// - R2-M13: AllowAutoRedirect=false on the underlying handler so manually-
    ///   attached Cookie / X-XSRF-TOKEN headers can't be replayed cross-origin
    ///   by a redirecting server.
    /// - R2-M14: MaxResponseContentBufferSize and Timeout defaults so a hostile
    ///   or compromised backend can't slow-drip multi-GB responses or hold
    ///   threads indefinitely.
    /// Callers who want different shapes can use the (HttpClient, ownsClient)
    /// ctor directly.
    /// </summary>
    private static HttpClient BuildDefaultHttpClient(Uri baseAddress)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 32 * 1024 * 1024, // 32 MiB
        };
        return client;
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
        ApplyViewerHeaders(request);
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
        // R2-M13 defense in depth: even if a caller built the client with
        // AllowAutoRedirect=true, refuse to absorb cookies from a cross-origin
        // redirect response. The Cookie + X-XSRF-TOKEN headers we manually
        // attach above are NOT stripped by HttpClient on redirect (unlike
        // Authorization since .NET 5), so allowing redirects to a foreign host
        // leaks the session.
        if (response.RequestMessage?.RequestUri is { } finalUri
            && _httpClient.BaseAddress is { } baseUri
            && !string.Equals(finalUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return response;
        }
        UpdateCookiesFromResponse(response);
        return response;
    }

    /// <summary>
    /// Attaches <see cref="TimeZoneId"/> and <see cref="AcceptLanguage"/> when they are set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>Called from two places, and it has to be.</b> <see cref="SendAsync"/> covers every typed
    /// method, but the antiforgery warmup builds its own request and goes straight to the inner
    /// <see cref="HttpClient"/> — a header attached in only one of them leaves the warmup GET
    /// unheadered, which is precisely the kind of asymmetry nobody notices until a culture-sensitive
    /// response comes back in the wrong language.
    /// </para>
    /// <para>
    /// ⚠️ Deliberately not <c>HttpClient.DefaultRequestHeaders</c>: the
    /// <c>(HttpClient, ownsClient: false)</c> ctor wraps a client the caller owns, and mutating its
    /// defaults would leak this client's settings into everything else using it.
    /// </para>
    /// </remarks>
    private void ApplyViewerHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(TimeZoneId))
            request.Headers.TryAddWithoutValidation("X-Spark-Timezone", TimeZoneId);
        if (!string.IsNullOrWhiteSpace(AcceptLanguage))
            request.Headers.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
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
    public Task<PersistentObject?> GetPersistentObjectAsync(
        Guid objectTypeId, string id, CancellationToken cancellationToken = default, SparkRetryHandler? onRetry = null, SparkOperationHandler? onOperation = null)
        => GetPersistentObjectCoreAsync(objectTypeId.ToString(), id, onRetry, onOperation, cancellationToken);

    // ⚠️ Every method below posts a JSON body to a literal path. Nothing is escaped into a URL any
    // more, which removes a whole class of bug rather than moving it: a Raven id contains slashes,
    // an alias is unvalidated, and both used to have to survive Uri.EscapeDataString and a route
    // template to arrive intact.

    /// <summary>
    /// Alias-based overload. <paramref name="aliasOrName"/> is resolved server-side to an
    /// entity type, so callers that only know the type by name (e.g. <c>"Person"</c>) don't
    /// need to look up its Guid first. Returns null on 404 (entity missing or row-level
    /// denied — the endpoint conflates these per security audit M-3).
    /// </summary>
    public Task<PersistentObject?> GetPersistentObjectAsync(
        string aliasOrName, string id, CancellationToken cancellationToken = default, SparkRetryHandler? onRetry = null, SparkOperationHandler? onOperation = null)
        => GetPersistentObjectCoreAsync(aliasOrName, id, onRetry, onOperation, cancellationToken);

    private Task<PersistentObject?> GetPersistentObjectCoreAsync(
        string objectTypeId, string id, SparkRetryHandler? onRetry, SparkOperationHandler? onOperation, CancellationToken cancellationToken)
        // A read can prompt too: OnLoadAsync is one of the nine hooks that may call Retry.Action, and
        // making reads POST is what bought the body this needs. ⚠️ A prompt from OnLoadAsync fires on
        // EVERY read of the type, so a handler that answers unconditionally is answering far more
        // often than a caller tends to expect.
        => PostConversationAsync<PersistentObject?>(
            "/spark/po/load",
            new Dictionary<string, object?> { ["objectTypeId"] = objectTypeId, ["id"] = id },
            // ⚠️ true even though this is a read. The endpoint carries no antiforgery metadata —
            // forging a read gains an attacker nothing, since the same-origin policy stops their
            // page from seeing the response — but since 11.0.0 Spark checks any mutating-verb
            // request under /spark that carries an ambient credential, and a cookie-authenticated
            // client is exactly that. Reads are POSTs here only because they need a body, which is
            // what puts them on the wrong side of a method-based gate. Priming is free after the
            // first call: the token is cached for the client's lifetime.
            requiresAntiforgery: true,
            async (response, ct) =>
            {
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                await SparkClientException.ThrowIfNotSuccessAsync(response, ct);
                return await response.Content.ReadFromJsonAsync<PersistentObject>(JsonOptions, ct);
            },
            onRetry,
            onOperation,
            cancellationToken);

    /// <summary>
    /// Creates a new PersistentObject. The instance's <see cref="PersistentObject.Id"/> must be
    /// null on input; the server assigns it and returns the populated object.
    /// </summary>
    /// <remarks>
    /// The Create endpoint returns the new <c>ClientOperationEnvelope</c> wire shape
    /// (<c>{ result, operations }</c>); this method unwraps the envelope and returns just the
    /// <see cref="PersistentObject"/>. Any client operations emitted by server-side action code
    /// (notify / navigate / refresh / disableAction) are currently dropped by this SDK — see
    /// docs/prd/PRD-ClientOperations.md.
    /// </remarks>
    public Task<PersistentObject> CreatePersistentObjectAsync(
        PersistentObject obj, CancellationToken cancellationToken = default, SparkRetryHandler? onRetry = null, SparkOperationHandler? onOperation = null)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return SendPersistentObjectAsync("/spark/po/create", obj.Name, id: null, obj, onRetry, onOperation, cancellationToken);
    }

    /// <summary>
    /// Updates an existing PersistentObject. The instance's <see cref="PersistentObject.Id"/> is
    /// required; <see cref="PersistentObject.Etag"/> is echoed back to the server for the
    /// optimistic-concurrency check — a stale etag surfaces as <see cref="SparkClientException"/>
    /// with <c>StatusCode = HttpStatusCode.Conflict</c>.
    /// </summary>
    public Task<PersistentObject> UpdatePersistentObjectAsync(
        PersistentObject obj, CancellationToken cancellationToken = default, SparkRetryHandler? onRetry = null, SparkOperationHandler? onOperation = null)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (string.IsNullOrEmpty(obj.Id))
            throw new ArgumentException("PersistentObject must have an Id for update.", nameof(obj));
        return SendPersistentObjectAsync("/spark/po/update", obj.ObjectTypeId.ToString(), obj.Id, obj, onRetry, onOperation, cancellationToken);
    }

    public Task DeletePersistentObjectAsync(
        Guid objectTypeId, string id, CancellationToken cancellationToken = default, SparkRetryHandler? onRetry = null, SparkOperationHandler? onOperation = null)
        => PostConversationAsync<object?>(
            "/spark/po/delete",
            new Dictionary<string, object?> { ["objectTypeId"] = objectTypeId.ToString(), ["id"] = id },
            requiresAntiforgery: true,
            async (response, ct) =>
            {
                await SparkClientException.ThrowIfNotSuccessAsync(response, ct);
                // Delete is enveloped, so a hook that notified on its way out is read here rather
                // than thrown away with the body.
                await ReadEnvelopeResultAsync<object>(response, onOperation, ct);
                return null;
            },
            onRetry,
            onOperation,
            cancellationToken);

    // ListPersistentObjectsAsync is gone, with the GET /spark/po/{type} endpoint it called. That was
    // a second list pipeline with no paging, no search, no sort and no take cap, beside a
    // /queries/{id}/execute that clamps take for exactly that reason. Use ExecuteQueryAsync against
    // the type's declared query instead — it is the same rows through the path that enforces.

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
        SortColumn[]? sortColumns = null,
        QueryColumnFilter[]? columns = null,
        CancellationToken cancellationToken = default,
        SparkRetryHandler? onRetry = null,
        SparkOperationHandler? onOperation = null)
        => ExecuteQueryCoreAsync(queryId.ToString(), skip, take, search, parentId, parentType, sortColumns, columns, onRetry, onOperation, cancellationToken);

    /// <summary>Executes a query by its alias (e.g. <c>"allpeople"</c>) instead of by Guid.</summary>
    public Task<QueryResult> ExecuteQueryAsync(
        string queryAlias,
        int skip = 0,
        int take = 50,
        string? search = null,
        string? parentId = null,
        string? parentType = null,
        SortColumn[]? sortColumns = null,
        QueryColumnFilter[]? columns = null,
        CancellationToken cancellationToken = default,
        SparkRetryHandler? onRetry = null,
        SparkOperationHandler? onOperation = null)
        => ExecuteQueryCoreAsync(queryAlias, skip, take, search, parentId, parentType, sortColumns, columns, onRetry, onOperation, cancellationToken);

    private Task<QueryResult> ExecuteQueryCoreAsync(
        string queryId, int skip, int take, string? search, string? parentId, string? parentType,
        SortColumn[]? sortColumns, QueryColumnFilter[]? columns,
        SparkRetryHandler? onRetry, SparkOperationHandler? onOperation, CancellationToken cancellationToken)
        // OnQueryAsync can prompt, so a list is a conversation too. ⚠️ Like OnLoadAsync, a prompt here
        // fires on every execution of the query — including the ones a grid issues while paging.
        => PostConversationAsync(
            "/spark/queries/execute",
            new Dictionary<string, object?>
            {
                ["queryId"] = queryId,
                ["skip"] = skip,
                ["take"] = take,
                ["search"] = search,
                ["parentId"] = parentId,
                ["parentType"] = parentType,
                ["sortColumns"] = sortColumns,
                ["columns"] = columns,
            },
            // true for the same reason as /spark/po/load above: a read, but a POST, and the gate is
            // method-based.
            requiresAntiforgery: true,
            async (response, ct) =>
            {
                await SparkClientException.ThrowIfNotSuccessAsync(response, ct);
                return await response.Content.ReadFromJsonAsync<QueryResult>(JsonOptions, ct)
                    ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty query response body.");
            },
            onRetry,
            onOperation,
            cancellationToken);

    /// <summary>
    /// The distinct values of one column, for a filter panel (#431).
    /// </summary>
    /// <remarks>
    /// <paramref name="columns"/> carries the OTHER columns' current filters, so the values returned
    /// are the ones still reachable — offering a value that empties the grid the moment it is picked
    /// is worse than omitting it.
    /// <para>
    /// An empty result means either "nothing matches" or "you may not enumerate this column", and the
    /// two are deliberately indistinguishable.
    /// </para>
    /// </remarks>
    public Task<DistinctValuesResult> GetDistinctValuesAsync(
        Guid queryId,
        string column,
        string? search = null,
        QueryColumnFilter[]? columns = null,
        string? parentId = null,
        string? parentType = null,
        CancellationToken cancellationToken = default)
        => GetDistinctValuesCoreAsync(queryId.ToString(), column, search, columns, parentId, parentType, cancellationToken);

    /// <summary>Alias-based overload for <see cref="GetDistinctValuesAsync(Guid,string,string?,QueryColumnFilter[]?,string?,string?,CancellationToken)"/>.</summary>
    public Task<DistinctValuesResult> GetDistinctValuesAsync(
        string queryAlias,
        string column,
        string? search = null,
        QueryColumnFilter[]? columns = null,
        string? parentId = null,
        string? parentType = null,
        CancellationToken cancellationToken = default)
        => GetDistinctValuesCoreAsync(queryAlias, column, search, columns, parentId, parentType, cancellationToken);

    private async Task<DistinctValuesResult> GetDistinctValuesCoreAsync(
        string queryId, string column, string? search, QueryColumnFilter[]? columns,
        string? parentId, string? parentType, CancellationToken cancellationToken)
    {
        var content = JsonContent.Create(
            new { queryId, column, search, columns, parentId, parentType }, options: JsonOptions);

        using var response = await SendAsync(
            HttpMethod.Post, "/spark/queries/distinct-values", content, cancellationToken: cancellationToken);

        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<DistinctValuesResult>(JsonOptions, cancellationToken)
            ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty distinct-values response body.");
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
        => GetQueryCoreAsync(alias, cancellationToken);

    private async Task<SparkQuery?> GetQueryCoreAsync(string queryId, CancellationToken cancellationToken)
    {
        var content = JsonContent.Create(new { queryId }, options: JsonOptions);
        using var response = await SendAsync(HttpMethod.Post, "/spark/queries/get", content, cancellationToken: cancellationToken);
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
    /// POSTs to <c>/spark/actions/execute</c>, naming the type and action in the body. Returns a
    /// <see cref="SparkActionResult"/> that distinguishes the server's in-protocol responses:
    /// empty-200 (action completed), 449 (retry-action — server is asking the caller a
    /// question). Actual failures (401/403/404/500) throw <see cref="SparkClientException"/>.
    /// </summary>
    public async Task<SparkActionResult> ExecuteActionAsync(
        Guid objectTypeId,
        string actionName,
        PersistentObject? parent = null,
        IReadOnlyList<string>? selectedItemIds = null,
        CancellationToken cancellationToken = default,
        string? parentId = null,
        string? parentType = null,
        string? queryId = null,
        SparkRetryHandler? onRetry = null,
        SparkOperationHandler? onOperation = null)
    {
        // parentId/parentType name a SUB-QUERY's container — a different type from this action's,
        // resolved server-side under its own Read gate. Distinct from `parent`, which is an object
        // of this action's own type. Appended after the token so existing positional calls keep
        // compiling; every caller in the repo passes by name anyway.
        //
        // ⚠️ queryId is what makes a grid invocation reproducible. The server re-runs the named query
        // narrowed to selectedItemIds and hands the action the rows the grid actually rendered; with
        // no query named it falls back to loading each id, which is a different code path with
        // different row filtering. The Angular grid always sends it
        // (spark-query-grid.component.ts:347-355) and this client could not, so a test over a
        // selection was asserting the path the grid never takes. Found by the S1 spike.
        var body = new Dictionary<string, object?>
        {
            ["objectTypeId"] = objectTypeId.ToString(),
            ["actionName"] = actionName,
            ["parent"] = parent,
            ["selectedItemIds"] = selectedItemIds,
            ["parentId"] = parentId,
            ["parentType"] = parentType,
            ["queryId"] = queryId,
        };

        // With a handler, the whole conversation runs inside one call, exactly as it does for every
        // other endpoint. Without one, a single attempt is made and a prompt comes back as a
        // *result* rather than an exception — an action is the one place where a caller routinely
        // wants to look at the question before answering it, and ContinueAsync is how they answer.
        if ((onRetry ?? RetryHandler) is not null)
        {
            return await PostConversationAsync(
                "/spark/actions/execute", body, requiresAntiforgery: true,
                async (response, ct) =>
                {
                    await SparkClientException.ThrowIfNotSuccessAsync(response, ct);
                    return SparkActionResult.ForSuccess((int)response.StatusCode);
                },
                onRetry, onOperation, cancellationToken);
        }

        return await PostActionOnceAsync(body, answers: [], onOperation, cancellationToken);
    }

    /// <summary>
    /// Answers the question in <paramref name="result"/> and resubmits. Returns the next outcome,
    /// which may itself be another prompt — a hook is free to ask more than once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The answer is <b>appended</b> to the ones already given, and the original request is sent
    /// again in full. The server replays the hook from the top on every attempt and feeds it the
    /// accumulated answers, so this is not a resumption of a suspended call — it is the same call,
    /// made again, knowing more. That is why nothing needs to be held open between the two.
    /// </para>
    /// <para>
    /// ⚠️ <paramref name="option"/> must be one of the options the prompt offered. Checked here
    /// rather than left to the server, which cannot tell an option that was never offered from one a
    /// hook stopped offering: both simply fail to match, and the hook runs its else-branch as though
    /// the caller had chosen something.
    /// </para>
    /// </remarks>
    /// <param name="persistentObject">
    /// The prompt's <see cref="RetryActionPayload.PersistentObject"/> with values filled in, when it
    /// carried one. Fleet's delete confirmation is the worked example: the server sends a form, and
    /// what comes back is what the hook compares the typed plate against.
    /// </param>
    public Task<SparkActionResult> ContinueAsync(
        SparkActionResult result,
        string option,
        PersistentObject? persistentObject = null,
        CancellationToken cancellationToken = default,
        SparkOperationHandler? onOperation = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(option);

        if (result.Retry is null || result.Body is null)
            throw new InvalidOperationException(
                "ContinueAsync needs a result that is asking a question. Check IsRetry first — a completed "
                + "action has nothing to continue.");

        if (result.Retry.Options.Length > 0 && !result.Retry.Options.Contains(option, StringComparer.Ordinal))
            throw new ArgumentException(
                $"\"{option}\" is not one of the options offered for step {result.Retry.Step} "
                + $"(\"{result.Retry.Title}\"): {string.Join(" / ", result.Retry.Options)}.", nameof(option));

        if (result.Answers.Count >= MaxRetryDepth)
            throw new SparkClientException((HttpStatusCode)449, null,
                $"Gave up after answering {MaxRetryDepth} prompts on /spark/actions/execute; the last was step "
                + $"{result.Retry.Step} (\"{result.Retry.Title}\"). A hook that re-raises regardless of the "
                + $"answer will do this. Raise {nameof(MaxRetryDepth)} if the conversation is genuinely this long.");

        // step comes from the prompt, never from Answers.Count — the server owns step numbering and a
        // hook may skip one.
        List<object> answers =
        [
            .. result.Answers,
            new { step = result.Retry.Step, option, persistentObject },
        ];

        return PostActionOnceAsync(result.Body, answers, onOperation, cancellationToken);
    }

    /// <summary>One attempt at the action endpoint, carrying whatever has been answered so far.</summary>
    private async Task<SparkActionResult> PostActionOnceAsync(
        Dictionary<string, object?> body, List<object> answers, SparkOperationHandler? onOperation, CancellationToken cancellationToken)
    {
        if (answers.Count > 0)
            body["retryResults"] = answers.ToArray();

        var sink = onOperation ?? OperationHandler;

        using var response = await SendAsync(
            HttpMethod.Post, "/spark/actions/execute", JsonContent.Create(body, options: JsonOptions),
            requiresAntiforgery: true, cancellationToken);

        // 449 (Retry With) is in-protocol; translate to a populated SparkActionResult rather
        // than throwing, because it's not an error — the server is asking a question.
        if ((int)response.StatusCode == 449)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            var operations = SparkClientOperations.Parse(raw);
            var retry = operations.OfType<SparkRetryOperation>().FirstOrDefault()?.Prompt
                ?? throw new SparkClientException(response.StatusCode, raw, "Empty retry-action response body.");

            // FR11: a caller that inspects IsRetry rather than answering through a handler still sees
            // what the hook said on its way to asking.
            var accompanying = operations.Where(o => o is not SparkRetryOperation).ToArray();
            if (sink is not null)
                foreach (var operation in accompanying)
                    sink(operation);

            return SparkActionResult.ForRetry(retry, body, answers, accompanying);
        }

        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);

        // The success body is an envelope too, and carries whatever the action asked the client to
        // do. ⚠️ Its `result` is always null today (ExecuteCustomAction envelopes a literal null),
        // so `operations` is the only part of it worth reading.
        var successBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var successOperations = SparkClientOperations.Parse(successBody);
        if (sink is not null)
            foreach (var operation in successOperations)
                sink(operation);

        return SparkActionResult.ForSuccess((int)response.StatusCode, successOperations);
    }

    // --------------------------------------------------------------------------------
    // The conversation loop
    // --------------------------------------------------------------------------------

    /// <summary>
    /// Posts <paramref name="body"/>, and while the server answers <c>449</c> asks
    /// <paramref name="onRetry"/> for an answer, appends it to the body's <c>retryResults</c> and
    /// posts the <b>same body</b> again — which is exactly what the Angular client does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>The body is resent whole, not diffed.</b> The server replays the hook from the top on
    /// every attempt and feeds it the accumulated answers; a request that carried only the answers
    /// would have nothing to replay. Confirmed on the wire by the S1 spike: across a three-attempt
    /// conversation the browser's body was identical but for a <c>retryResults</c> array that grew by
    /// one each time.
    /// </para>
    /// <para>
    /// ⚠️ <b><c>step</c> is echoed from the prompt, never counted here.</b> The server owns step
    /// numbering, and a hook is free to skip one — a confirmation that only asks the second question
    /// when the first was answered a particular way. A locally incremented counter agrees with the
    /// server right up until that happens, and then silently answers the wrong question.
    /// </para>
    /// </remarks>
    private async Task<T> PostConversationAsync<T>(
        string url,
        Dictionary<string, object?> body,
        bool requiresAntiforgery,
        Func<HttpResponseMessage, CancellationToken, Task<T>> readResult,
        SparkRetryHandler? onRetry,
        SparkOperationHandler? onOperation,
        CancellationToken cancellationToken)
    {
        var handler = onRetry ?? RetryHandler;
        var sink = onOperation ?? OperationHandler;
        var answers = new List<object>();

        for (var attempt = 0; ; attempt++)
        {
            using var response = await SendAsync(
                HttpMethod.Post, url, JsonContent.Create(body, options: JsonOptions), requiresAntiforgery, cancellationToken);

            if ((int)response.StatusCode != 449)
                return await readResult(response, cancellationToken);

            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            var prompt = ReadPromptAndSurfaceOperations(raw, sink)
                ?? throw new SparkClientException(response.StatusCode, raw,
                    "The server answered 449 with no retry operation in the envelope.");

            if (handler is null)
                throw new SparkRetryRequiredException(prompt, raw, answers.Count);

            if (attempt >= MaxRetryDepth)
                throw new SparkClientException(response.StatusCode, raw,
                    $"Gave up after answering {MaxRetryDepth} prompts on {url}; the last was step {prompt.Step} " +
                    $"(\"{prompt.Title}\"). A hook that re-raises regardless of the answer will do this. " +
                    $"Raise {nameof(MaxRetryDepth)} if the conversation is genuinely this long.");

            var answer = await handler(prompt, cancellationToken)
                ?? throw new SparkRetryRequiredException(prompt, raw, answers.Count);

            // FR6. Checked here rather than left to the server because the server has no way to tell
            // an option it never offered from one a hook stopped offering between attempts: both
            // simply fail to match, and the hook then runs its else-branch as if the user had chosen
            // something. A typo in a test would silently assert the wrong path.
            if (prompt.Options.Length > 0 && !prompt.Options.Contains(answer.Option, StringComparer.Ordinal))
                throw new ArgumentException(
                    $"\"{answer.Option}\" is not one of the options offered for step {prompt.Step} " +
                    $"(\"{prompt.Title}\"): {string.Join(" / ", prompt.Options)}.", nameof(onRetry));

            answers.Add(new { step = prompt.Step, option = answer.Option, persistentObject = answer.PersistentObject });
            body["retryResults"] = answers.ToArray();
        }
    }

    /// <summary>
    /// Parses a <c>449</c> envelope into the question it asks and the operations that accompany it,
    /// and hands every non-retry operation to <paramref name="sink"/> <b>before returning</b> — FR11.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>One pass, both halves.</b> A hook that says something and then asks a question emits
    /// both in the same envelope, so parsing the prompt separately from the operations would read
    /// the body twice and deserialize the prompt's nested PersistentObject once per attempt.
    /// </para>
    /// <para>
    /// The prompt is null when the envelope carries no <c>retry</c> operation — a malformed 449,
    /// not an ordinary outcome.
    /// </para>
    /// </remarks>
    private static RetryActionPayload? ReadPromptAndSurfaceOperations(string envelopeJson, SparkOperationHandler? sink)
    {
        var operations = SparkClientOperations.Parse(envelopeJson);

        // Every non-retry operation, in emission order — not just the ones ahead of the retry. The
        // frontend filters rather than splitting at the prompt (spark.service.ts), and a hook is
        // free to notify after raising.
        if (sink is not null)
            foreach (var operation in operations)
                if (operation is not SparkRetryOperation)
                    sink(operation);

        return operations.OfType<SparkRetryOperation>().FirstOrDefault()?.Prompt;
    }

    // --------------------------------------------------------------------------------
    // CSRF / internals
    // --------------------------------------------------------------------------------

    private Task<PersistentObject> SendPersistentObjectAsync(
        string url, string? objectTypeId, string? id, PersistentObject obj, SparkRetryHandler? onRetry, SparkOperationHandler? onOperation, CancellationToken cancellationToken)
        // objectTypeId is the request parameter; obj.ObjectTypeId travels inside the document and is
        // overwritten server-side with whatever this one resolves to. They are separate fields on
        // purpose — see SparkRequestType.
        => PostConversationAsync(
            url,
            new Dictionary<string, object?> { ["objectTypeId"] = objectTypeId, ["id"] = id, ["persistentObject"] = obj },
            requiresAntiforgery: true,
            async (response, ct) =>
            {
                await SparkClientException.ThrowIfNotSuccessAsync(response, ct);
                return await ReadEnvelopeResultAsync<PersistentObject>(response, onOperation, ct)
                    ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty response body.");
            },
            onRetry,
            onOperation,
            cancellationToken);

    /// <summary>
    /// Reads a <c>{ result, operations }</c> envelope (per PRD-ClientOperations): hands every
    /// operation to the sink, and returns the typed <c>result</c> field — <c>default</c> when it is
    /// null or absent.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Only the seven enveloped endpoints may go through this.</b> A bare-JSON endpoint has no
    /// <c>result</c> property, so this would return <c>default</c> for every call — silently, with
    /// no exception to notice.
    /// </remarks>
    private async Task<T?> ReadEnvelopeResultAsync<T>(
        HttpResponseMessage response, SparkOperationHandler? onOperation, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        var sink = onOperation ?? OperationHandler;
        if (sink is not null)
            foreach (var operation in SparkClientOperations.Parse(body))
                sink(operation);

        if (string.IsNullOrWhiteSpace(body)) return default;
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return default;
        if (!doc.RootElement.TryGetProperty("result", out var resultEl)) return default;
        if (resultEl.ValueKind == JsonValueKind.Null) return default;
        return resultEl.Deserialize<T>(JsonOptions);
    }

    /// <summary>
    /// Ensures <see cref="_xsrfToken"/> is populated. If the server previously returned one
    /// via Set-Cookie (on any read), the token is already cached. Otherwise, fires a warmup
    /// GET to <c>/spark</c> — the health check — which always mints the antiforgery cookie pair.
    /// </summary>
    /// <remarks>
    /// ⚠️ It used to warm up on <c>GET /spark/po/__warmup__</c>, a deliberate miss against the
    /// catch-all load route. That route is gone: loads are <c>POST /spark/po/load</c> now, and there
    /// is no path left that a nonsense id can land on. The health check is a real endpoint that
    /// answers unauthenticated, which is what this needs and all it needs.
    /// </remarks>
    private async Task EnsureAntiforgeryAsync(CancellationToken cancellationToken)
    {
        if (_xsrfToken is not null) return;

        var warmupRequest = new HttpRequestMessage(HttpMethod.Get, "/spark");
        ApplyViewerHeaders(warmupRequest);
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
        // R2-L5: reject Set-Cookie entries whose Domain attribute targets a host
        // other than BaseAddress. The previous parser stripped attributes
        // entirely, so a misconfigured or hostile server could pin a cookie
        // value for any domain in our jar. Validate before applying.
        var baseHost = _httpClient.BaseAddress?.Host;
        foreach (var raw in setCookies)
        {
            // Reject cookies that scope themselves to a different domain than the
            // configured base address. Most production responses omit Domain (so
            // the cookie defaults to the host it came from), which is fine.
            if (baseHost is not null && TryGetDomainAttribute(raw, out var domain)
                && !string.Equals(domain, baseHost, StringComparison.OrdinalIgnoreCase)
                && !baseHost.EndsWith("." + domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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

    private static bool TryGetDomainAttribute(string setCookie, out string domain)
    {
        domain = string.Empty;
        var parts = setCookie.Split(';');
        for (int i = 1; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            if (part.StartsWith("Domain=", StringComparison.OrdinalIgnoreCase))
            {
                domain = part["Domain=".Length..].Trim().TrimStart('.');
                return !string.IsNullOrEmpty(domain);
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}
