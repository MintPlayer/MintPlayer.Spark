using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Http;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Mcp;

/// <summary>
/// Client ID Metadata Documents (MCP PRD D5, gap G5; draft-ietf-oauth-client-id-metadata-document): a client id that
/// is an HTTPS URL names a JSON document, fetched from that URL, that describes the client. Claude Code, Codex and VS
/// Code sign in this way, with no registration step.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fetch is an SSRF channel</b>, started by an anonymous <c>/connect/authorize</c>: the URL is the caller's
/// choice. So it goes through the SSRF-safe client (public addresses only, checked and dialled from one DNS
/// resolution so a rebinding answer has nothing to change, port 443 only, no redirects, size cap, timeout), after
/// a strict check of the URL itself, an optional host allow-list and a per-address budget.
/// </para>
/// <para>
/// <b>Materialised as an application</b> with a natural id (<see cref="OidcAgentClients.ApplicationId"/>), so every
/// other endpoint (token, revocation, introspection) finds the client by its id exactly as it finds any other, and the
/// document is fetched again only when the stored copy is older than
/// <see cref="SparkIdentityProviderMcpOptions.MetadataDocumentCacheFor"/>.
/// </para>
/// </remarks>
internal sealed partial class OidcClientMetadataDocuments(
    IDocumentStore store,
    IHttpClientFactory httpClients,
    SparkIdentityProviderOptions options,
    OidcAgentRegistrationLimiter limiter,
    OidcAudit audit,
    ILogger<OidcClientMetadataDocuments> logger)
{
    /// <summary>The SSRF-safe named client that fetches metadata documents.</summary>
    public const string HttpClientName = "Spark.IdentityProvider.ClientMetadataDocument";

    private const int MaxClientIdLength = 512;
    private const int MaxRedirectUris = 20;

    /// <summary>Whether <paramref name="clientId"/> is meant as a metadata-document URL (it is then never looked up any other way).</summary>
    public static bool IsDocumentClientId(string? clientId) => clientId is not null && clientId.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The refusal for a client id URL, or <see langword="null"/> when it may be fetched: HTTPS, a host on the allow-list
    /// (when there is one), the default port, a path, and no user info, query, fragment or dot segments.
    /// </summary>
    public string? CheckClientId(string clientId)
    {
        if (clientId.Length > MaxClientIdLength || clientId.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            return "The client id URL is not valid.";
        if (!Uri.TryCreate(clientId, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            return "A client id URL must be an https URL.";
        if (url.UserInfo.Length > 0 || clientId.Contains('@'))
            return "A client id URL must not contain user information.";
        if (clientId.Contains('#') || clientId.Contains('?'))
            return "A client id URL must not contain a query or a fragment.";
        if (!url.IsDefaultPort)
            return "A client id URL must use the default https port.";

        // The path, from the original string: Uri normalises dot segments away, which is exactly what must be refused.
        var path = clientId[(clientId.IndexOf('/', "https://".Length) is var slash and >= 0 ? slash : clientId.Length)..];
        if (path.Length <= 1)
            return "A client id URL must have a path.";
        if (path.Split('/').Any(segment => segment is "." or ".." || segment.Equals("%2e", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("%2e%2e", StringComparison.OrdinalIgnoreCase) || segment.Equals(".%2e", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("%2e.", StringComparison.OrdinalIgnoreCase)))
            return "A client id URL must not contain dot segments.";
        // The agent grant's natural id ends with the client id (AgentGrants/{userId}/app/{clientId}), and a RavenDB id
        // ending in '/' or '|' asks the server to generate one.
        if (path.EndsWith('/') || path.EndsWith('|'))
            return "A client id URL must not end with '/'.";

        var hosts = options.Mcp?.MetadataDocumentHosts ?? [];
        if (hosts.Count > 0 && !hosts.Contains(url.IdnHost, StringComparer.OrdinalIgnoreCase))
            return "Client metadata documents from this host are not accepted.";
        return null;
    }

    /// <summary>
    /// The application for metadata-document client <paramref name="clientId"/>, fetched now when the stored one is
    /// missing or older than the freshness window; or the reason it cannot be used (answered as <c>invalid_client</c>).
    /// </summary>
    public async Task<(OidcApplication? Application, string? Error)> ResolveAsync(string clientId, string? address, CancellationToken ct)
    {
        var mcp = options.Mcp ?? throw new InvalidOperationException("Client metadata documents need idp.ForMcp().");
        if (CheckClientId(clientId) is { } invalid)
            return (null, invalid);

        using var session = store.OpenAsyncSession();
        var id = OidcAgentClients.ApplicationId(clientId);
        var existing = await session.LoadAsync<OidcApplication>(id, ct);
        if (existing is not null && !string.Equals(existing.ClientId, clientId, StringComparison.Ordinal))
            return (null, "Unknown client.");
        if (existing is not null && existing.AgentRegistration != OidcAgentRegistrations.MetadataDocument)
            return (existing, null); // a pre-registered client with this id: the administrator's record wins.
        if (existing?.MetadataDocumentFetchedAt is { } fetched && DateTime.UtcNow - fetched < mcp.MetadataDocumentCacheFor)
            return (existing, null);

        if (!limiter.TryAcquire(address))
            return (null, "Too many client registrations from this address. Try again later.");

        var (document, error) = await FetchAsync(clientId, mcp, ct);
        if (document is null)
        {
            LogRefused(logger, clientId, error!);
            return (null, error);
        }

        var app = existing ?? new OidcApplication { Id = id, ClientId = clientId, CreatedAt = DateTime.UtcNow, Enabled = true };
        OidcAgentClients.ApplyAgentDefaults(app, mcp);
        app.AgentRegistration = OidcAgentRegistrations.MetadataDocument;
        app.DisplayName = OidcAgentClients.NameOf(document.ClientName, new Uri(clientId).Host);
        app.HomepageUrl = document.ClientUri;
        app.RedirectUris = document.RedirectUris;
        app.MetadataDocumentFetchedAt = DateTime.UtcNow;
        app.LastUsedAt ??= DateTime.UtcNow;

        if (existing is null)
        {
            try
            {
                await OidcClientIdReservation.ReserveAsync(session, app);
            }
            catch (Abstractions.SparkValidationException)
            {
                return (null, "Unknown client.");
            }
            await session.StoreAsync(app, ct);
        }

        await audit.RecordAsync(session, OidcAuditKinds.MetadataDocumentRegistration, actorId: null, app.Id, ipAddress: address,
            details: new Dictionary<string, string> { ["clientId"] = clientId, ["created"] = (existing is null).ToString() }, ct: ct);
        await session.SaveChangesAsync(ct);
        return (app, null);
    }

    /// <summary>Fetches and validates the document; the refusal names what was wrong and never echoes the body.</summary>
    internal async Task<(ClientMetadataDocument? Document, string? Error)> FetchAsync(string clientId, SparkIdentityProviderMcpOptions mcp, CancellationToken ct)
    {
        byte[] body;
        try
        {
            var http = httpClients.CreateClient(HttpClientName);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(mcp.MetadataDocumentTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, clientId);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
                return (null, "The client metadata document could not be fetched.");
            if (response.Content.Headers.ContentLength is { } length && length > mcp.MetadataDocumentMaxBytes)
                return (null, "The client metadata document is too large.");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var copy = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                copy.Write(buffer, 0, read);
                if (copy.Length > mcp.MetadataDocumentMaxBytes)
                    return (null, "The client metadata document is too large.");
            }
            body = copy.ToArray();
        }
        catch (Exception exception) when (!ct.IsCancellationRequested
            && exception is SafeHttpBlockedException or HttpRequestException or OperationCanceledException or IOException)
        {
            // A block in the connect callback (a host name resolving to a private address) arrives wrapped.
            var blocked = exception as SafeHttpBlockedException ?? exception.InnerException as SafeHttpBlockedException;
            return (null, blocked?.Reason == SafeHttpBlockReason.Address
                ? "The client metadata document's host is not a public address."
                : "The client metadata document could not be fetched.");
        }

        return Parse(clientId, body);
    }

    /// <summary>Validates the document's content (exposed for tests).</summary>
    internal static (ClientMetadataDocument? Document, string? Error) Parse(string clientId, byte[] body)
    {
        JsonElement root;
        try
        {
            using var json = JsonDocument.Parse(body);
            root = json.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (null, "The client metadata document is not JSON.");
        }
        if (root.ValueKind != JsonValueKind.Object)
            return (null, "The client metadata document is not a JSON object.");

        // The document must name itself: a document served at one URL that claims another client id would let any
        // host speak for that client.
        if (String(root, "client_id") is not { } named || !string.Equals(named, clientId, StringComparison.Ordinal))
            return (null, "The client metadata document does not name its own URL as client_id.");

        // Public clients only: there is nothing a document could hold that this server could verify as a secret.
        if (String(root, "token_endpoint_auth_method") is { } method && method != OidcClientAuthMethods.None)
            return (null, "Only public clients (token_endpoint_auth_method \"none\") may use a client metadata document.");
        if (root.TryGetProperty("client_secret", out _))
            return (null, "A client metadata document must not contain a client secret.");
        if (Strings(root, "grant_types") is { } grants && !grants.Contains("authorization_code"))
            return (null, "The client metadata document does not allow the authorization_code grant.");
        if (Strings(root, "response_types") is { } responses && !responses.Contains("code"))
            return (null, "The client metadata document does not allow the code response type.");

        var redirects = Strings(root, "redirect_uris");
        if (redirects is not { Count: > 0 and <= MaxRedirectUris })
            return (null, "The client metadata document must list between 1 and 20 redirect_uris.");
        foreach (var redirect in redirects)
        {
            // https, or loopback http (RFC 8252); no custom schemes, no fragments.
            var ok = OidcAgentClients.IsLoopback(redirect)
                     || (Uri.TryCreate(redirect, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                         && uri.UserInfo.Length == 0 && !redirect.Contains('#'));
            if (!ok)
                return (null, "A redirect URI in the client metadata document is not https or loopback.");
        }

        var clientUri = String(root, "client_uri");
        if (clientUri is not null && (!Uri.TryCreate(clientUri, UriKind.Absolute, out var home) || home.Scheme != Uri.UriSchemeHttps))
            clientUri = null;

        return (new ClientMetadataDocument(String(root, "client_name"), clientUri, redirects), null);
    }

    private static string? String(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string>? Strings(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
            : null;

    [LoggerMessage(EventId = 7401, EventName = "SparkClientMetadataDocumentRefused", Level = LogLevel.Information,
        Message = "Client metadata document {ClientId} refused: {Reason}")]
    private static partial void LogRefused(ILogger logger, string clientId, string reason);
}

/// <summary>What a valid metadata document contributes to the client's application.</summary>
internal sealed record ClientMetadataDocument(string? ClientName, string? ClientUri, List<string> RedirectUris);
