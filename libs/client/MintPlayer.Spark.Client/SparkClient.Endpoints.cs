using System.Net;
using System.Net.Http.Json;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Client;

/// <summary>
/// M5 — the rest of the server's surface, so a caller never has to drop to
/// <see cref="SparkClient.SendAsync"/> to reach an ordinary endpoint.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Only <c>refresh</c>, <c>new</c> and <c>delete-row</c> are enveloped, and only they can
/// answer <c>449</c>.</b> Everything else in this file returns bare JSON, so it goes through
/// <see cref="SparkClient.SendAsync"/> directly and takes no <c>onRetry</c> — offering one would
/// advertise a conversation the server cannot start.
/// </para>
/// <para>
/// ⚠️ <b>These are also the endpoints that still carry route variables.</b> The literal route table
/// M2 built covers <c>/po</c>, <c>/queries</c> and <c>/actions</c>; <c>lookupref/{name}</c>,
/// <c>lookupref/{name}/{key}</c> and <c>types/{id}</c> keep theirs, and a lookup-reference key is
/// user data — hence <see cref="Uri.EscapeDataString"/> on every segment below.
/// </para>
/// </remarks>
public partial class SparkClient
{
    // --------------------------------------------------------------------------------
    // PersistentObject — the three verbs beyond CRUD
    // --------------------------------------------------------------------------------

    /// <summary>
    /// Sends an in-progress object back for the server to reshape — the <c>OnRefreshAsync</c> hook
    /// recomputes values, labels, rules and visibility from what the user has typed so far.
    /// </summary>
    /// <param name="triggeredBy">
    /// The attribute whose change prompted this, so the hook can tell a targeted edit from a
    /// wholesale refresh.
    /// </param>
    /// <remarks>
    /// Writes nothing, so the object's <see cref="PersistentObject.Etag"/> stays valid across a
    /// refresh and the edit session continues against the same document version.
    /// </remarks>
    public Task<PersistentObject> RefreshPersistentObjectAsync(
        PersistentObject obj,
        string triggeredBy,
        CancellationToken cancellationToken = default,
        SparkRetryHandler? onRetry = null,
        SparkOperationHandler? onOperation = null)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return PostConversationAsync(
            "/spark/po/refresh",
            new Dictionary<string, object?>
            {
                ["objectTypeId"] = obj.ObjectTypeId.ToString(),
                ["persistentObject"] = obj,
                ["triggeredBy"] = triggeredBy,
            },
            requiresAntiforgery: true,
            async (response, ct) =>
            {
                await SparkClientException.ThrowIfNotSuccessAsync(response, ct);
                return await ReadEnvelopeResultAsync<PersistentObject>(response, onOperation, ct)
                    ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty refresh response body.");
            },
            onRetry,
            onOperation,
            cancellationToken);
    }

    /// <summary>
    /// Scaffolds an unsaved object of <paramref name="objectTypeId"/> — the server fills in
    /// defaults, rules and metadata so a caller edits a shaped object instead of inventing one.
    /// </summary>
    /// <param name="asDetailAttribute">
    /// When the new object is a row of a detail grid, the attribute it belongs to;
    /// <paramref name="parentType"/> and <paramref name="parentId"/> name the owner.
    /// </param>
    public Task<PersistentObject> NewPersistentObjectAsync(
        Guid objectTypeId,
        string? asDetailAttribute = null,
        string? parentType = null,
        string? parentId = null,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default,
        SparkRetryHandler? onRetry = null,
        SparkOperationHandler? onOperation = null)
        => NewPersistentObjectCoreAsync(
            objectTypeId.ToString(), asDetailAttribute, parentType, parentId, parameters, onRetry, onOperation, cancellationToken);

    /// <summary>Alias-based overload of <see cref="NewPersistentObjectAsync(Guid,string?,string?,string?,IReadOnlyDictionary{string,string}?,CancellationToken,SparkRetryHandler?,SparkOperationHandler?)"/>.</summary>
    public Task<PersistentObject> NewPersistentObjectAsync(
        string aliasOrName,
        string? asDetailAttribute = null,
        string? parentType = null,
        string? parentId = null,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default,
        SparkRetryHandler? onRetry = null,
        SparkOperationHandler? onOperation = null)
        => NewPersistentObjectCoreAsync(
            aliasOrName, asDetailAttribute, parentType, parentId, parameters, onRetry, onOperation, cancellationToken);

    private Task<PersistentObject> NewPersistentObjectCoreAsync(
        string objectTypeId, string? asDetailAttribute, string? parentType, string? parentId,
        IReadOnlyDictionary<string, string>? parameters,
        SparkRetryHandler? onRetry, SparkOperationHandler? onOperation, CancellationToken cancellationToken)
        => PostConversationAsync(
            "/spark/po/new",
            new Dictionary<string, object?>
            {
                ["objectTypeId"] = objectTypeId,
                ["asDetailAttribute"] = asDetailAttribute,
                ["parentType"] = parentType,
                ["parentId"] = parentId,
                ["parameters"] = parameters,
            },
            requiresAntiforgery: true,
            async (response, ct) =>
            {
                await SparkClientException.ThrowIfNotSuccessAsync(response, ct);
                return await ReadEnvelopeResultAsync<PersistentObject>(response, onOperation, ct)
                    ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty new response body.");
            },
            onRetry,
            onOperation,
            cancellationToken);

    /// <summary>
    /// Removes one row from a detail grid, by the row key the grid carries.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="DeletePersistentObjectAsync"/>: this deletes a row <b>within</b> an
    /// owner, and the owner is what gets written.
    /// </remarks>
    public Task DeleteRowAsync(
        Guid objectTypeId,
        string asDetailAttribute,
        string parentType,
        string parentId,
        string rowKey,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default,
        SparkRetryHandler? onRetry = null,
        SparkOperationHandler? onOperation = null)
        => PostConversationAsync<object?>(
            "/spark/po/delete-row",
            new Dictionary<string, object?>
            {
                ["objectTypeId"] = objectTypeId.ToString(),
                ["asDetailAttribute"] = asDetailAttribute,
                ["parentType"] = parentType,
                ["parentId"] = parentId,
                ["rowKey"] = rowKey,
                ["parameters"] = parameters,
            },
            requiresAntiforgery: true,
            async (response, ct) =>
            {
                await SparkClientException.ThrowIfNotSuccessAsync(response, ct);
                await ReadEnvelopeResultAsync<object>(response, onOperation, ct);
                return null;
            },
            onRetry,
            onOperation,
            cancellationToken);

    // --------------------------------------------------------------------------------
    // Lookup references — route variables, no envelope, no retry
    // --------------------------------------------------------------------------------

    /// <summary>Lists every lookup reference, without their values.</summary>
    public async Task<IReadOnlyList<LookupReferenceListItem>> ListLookupReferencesAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/spark/lookupref/", cancellationToken: cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<LookupReferenceListItem[]>(JsonOptions, cancellationToken)
            ?? [];
    }

    /// <summary>Returns one lookup reference with its values, or null when there is no such reference.</summary>
    public async Task<LookupReferenceDto?> GetLookupReferenceAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        using var response = await SendAsync(
            HttpMethod.Get, $"/spark/lookupref/{Uri.EscapeDataString(name)}", cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<LookupReferenceDto>(JsonOptions, cancellationToken);
    }

    /// <summary>Adds a value to a lookup reference. The server answers <c>201 Created</c>.</summary>
    /// <remarks>
    /// ⚠️ Transient (code-declared) references reject writes — that arrives as a
    /// <see cref="SparkClientException"/> with <c>StatusCode = BadRequest</c> and the reason in
    /// <see cref="SparkClientException.ResponseBody"/>, not as a typed failure.
    /// </remarks>
    public async Task<LookupReferenceValueDto> AddLookupReferenceValueAsync(
        string name, LookupReferenceValueDto value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        using var response = await SendAsync(
            HttpMethod.Post, $"/spark/lookupref/{Uri.EscapeDataString(name)}",
            JsonContent.Create(value, options: JsonOptions), requiresAntiforgery: true, cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<LookupReferenceValueDto>(JsonOptions, cancellationToken)
            ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty lookup-reference response body.");
    }

    /// <summary>Updates one value of a lookup reference, by key.</summary>
    public async Task<LookupReferenceValueDto> UpdateLookupReferenceValueAsync(
        string name, string key, LookupReferenceValueDto value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        using var response = await SendAsync(
            HttpMethod.Put, $"/spark/lookupref/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(key)}",
            JsonContent.Create(value, options: JsonOptions), requiresAntiforgery: true, cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<LookupReferenceValueDto>(JsonOptions, cancellationToken)
            ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty lookup-reference response body.");
    }

    /// <summary>Deletes one value of a lookup reference, by key. The server answers <c>204</c>.</summary>
    public async Task DeleteLookupReferenceValueAsync(string name, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(key);
        using var response = await SendAsync(
            HttpMethod.Delete, $"/spark/lookupref/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(key)}",
            requiresAntiforgery: true, cancellationToken: cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
    }

    // --------------------------------------------------------------------------------
    // Metadata
    // --------------------------------------------------------------------------------

    /// <summary>
    /// Returns one entity type definition — by id, name or alias — or null when the caller may not
    /// see it.
    /// </summary>
    /// <remarks>
    /// ⚠️ Null means "no type you are allowed to know about". The endpoint conflates missing with
    /// denied on purpose, so that a 404-vs-403 difference cannot be used to enumerate types.
    /// </remarks>
    public async Task<EntityTypeDefinition?> GetEntityTypeAsync(string idOrNameOrAlias, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(idOrNameOrAlias);
        using var response = await SendAsync(
            HttpMethod.Get, $"/spark/types/{Uri.EscapeDataString(idOrNameOrAlias)}", cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<EntityTypeDefinition>(JsonOptions, cancellationToken);
    }

    /// <summary>
    /// Lists the custom actions the caller may invoke on an entity type, already ordered by
    /// <see cref="SparkCustomAction.Offset"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>An empty list is the answer for an unknown type and for a denied one alike</b> — the
    /// endpoint answers 200 either way, never 404, because the difference is an existence oracle.
    /// Do not read empty as "no such type".
    /// </remarks>
    public Task<IReadOnlyList<SparkCustomAction>> ListCustomActionsAsync(Guid objectTypeId, CancellationToken cancellationToken = default)
        => ListCustomActionsCoreAsync(objectTypeId.ToString(), cancellationToken);

    /// <summary>Alias-based overload of <see cref="ListCustomActionsAsync(Guid,CancellationToken)"/>.</summary>
    public Task<IReadOnlyList<SparkCustomAction>> ListCustomActionsAsync(string aliasOrName, CancellationToken cancellationToken = default)
        => ListCustomActionsCoreAsync(aliasOrName, cancellationToken);

    private async Task<IReadOnlyList<SparkCustomAction>> ListCustomActionsCoreAsync(string objectTypeId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post, "/spark/actions/list",
            JsonContent.Create(new { objectTypeId }, options: JsonOptions), cancellationToken: cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SparkCustomAction[]>(JsonOptions, cancellationToken) ?? [];
    }

    /// <summary>Returns the program-unit configuration — the application's navigation, as the caller sees it.</summary>
    public async Task<ProgramUnitsConfiguration> GetProgramUnitsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/spark/program-units", cancellationToken: cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ProgramUnitsConfiguration>(JsonOptions, cancellationToken)
            ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty /spark/program-units response.");
    }

    /// <summary>Returns the configured languages and the default one.</summary>
    public async Task<CultureConfiguration> GetCultureAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/spark/culture", cancellationToken: cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<CultureConfiguration>(JsonOptions, cancellationToken)
            ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty /spark/culture response.");
    }

    /// <summary>Returns the whole translation catalogue, keyed by translation key.</summary>
    /// <remarks>
    /// ⚠️ The catalogue is anonymous and unfiltered by design — see <c>docs/leftovers.md</c>. It is
    /// the label text of every type in the application, so treat it as public.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, TranslatedString>> GetTranslationsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/spark/translations", cancellationToken: cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<Dictionary<string, TranslatedString>>(JsonOptions, cancellationToken)
            ?? new Dictionary<string, TranslatedString>();
    }
}
