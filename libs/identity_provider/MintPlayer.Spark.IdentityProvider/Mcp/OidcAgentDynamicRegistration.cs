using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Mcp;

/// <summary>
/// Anonymous dynamic registration for AI agents (RFC 7591 without an initial access token; MCP PRD D5, gap G6): what
/// Gemini CLI does, and Claude Code and Codex where a metadata document is not used.
/// </summary>
/// <remarks>
/// Safe without anyone vouching for the client because every redirect URI must be loopback (or VS Code's own,
/// <see cref="OidcAgentClients.KnownRedirectUris"/>): the code can reach only a program on the user's own computer,
/// and the consent screen says so. The client is public (PKCE, no secret), labelled unverified, limited per address,
/// gets no registration access token (nothing to manage), and is deleted after
/// <see cref="SparkIdentityProviderMcpOptions.UnusedClientLifetime"/> without use.
/// </remarks>
internal sealed class OidcAgentDynamicRegistration(
    IDocumentStore store,
    SparkIdentityProviderOptions options,
    OidcAgentRegistrationLimiter limiter,
    OidcAudit audit)
{
    private const int MaxRedirectUris = 10;

    public async Task<IResult> RegisterAsync(OidcClientMetadata metadata, string? address, CancellationToken ct)
    {
        var mcp = options.Mcp!;

        var redirects = metadata.RedirectUris ?? [];
        if (redirects.Count is 0 or > MaxRedirectUris)
            return Invalid("invalid_redirect_uri", $"Between 1 and {MaxRedirectUris} redirect_uris are required.");
        if (redirects.Any(r => !OidcAgentClients.AcceptableWithoutRegistrant(r)))
        {
            return Invalid("invalid_redirect_uri",
                "Registration without an initial access token accepts loopback redirect URIs only (http://localhost, http://127.0.0.1 or http://[::1], RFC 8252).");
        }
        if (metadata.TokenEndpointAuthMethod is { } method && method != OidcClientAuthMethods.None)
            return Invalid("invalid_client_metadata", "Registration without an initial access token registers public clients only (token_endpoint_auth_method \"none\").");
        if (metadata.GrantTypes is { } grants && grants.Any(g => g is not ("authorization_code" or "refresh_token")))
            return Invalid("invalid_client_metadata", "Only the authorization_code and refresh_token grants are available.");
        if (metadata.ResponseTypes is { } responses && responses.Any(r => r != "code"))
            return Invalid("invalid_client_metadata", "Only the code response type is available.");

        if (!limiter.TryAcquire(address))
            return Results.Json(new { error = "too_many_requests", error_description = "Too many client registrations from this address. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests);

        var clientId = OidcAgentClients.DynamicClientIdPrefix + OidcClientIdReservation.GenerateClientId();
        var now = DateTime.UtcNow;
        var app = new OidcApplication
        {
            Id = OidcAgentClients.ApplicationId(clientId),
            ClientId = clientId,
            CreatedAt = now,
            LastUsedAt = now,
            Enabled = true,
        };
        OidcAgentClients.ApplyAgentDefaults(app, mcp);
        app.AgentRegistration = OidcAgentRegistrations.DynamicRegistration;
        app.DisplayName = OidcAgentClients.NameOf(metadata.ClientName, "Unnamed agent");
        app.RedirectUris = [.. redirects.Distinct(StringComparer.Ordinal)];

        using var session = store.OpenAsyncSession();
        await OidcClientIdReservation.ReserveAsync(session, app);
        await session.StoreAsync(app, ct);
        await audit.RecordAsync(session, OidcAuditKinds.DynamicRegistration, actorId: null, app.Id, ipAddress: address,
            details: new Dictionary<string, string> { ["agent"] = "true", ["clientName"] = app.DisplayName }, ct: ct);
        await session.SaveChangesAsync(ct);

        return Results.Json(new Dictionary<string, object?>
        {
            ["client_id"] = app.ClientId,
            ["client_id_issued_at"] = new DateTimeOffset(now).ToUnixTimeSeconds(),
            ["client_name"] = app.DisplayName,
            ["redirect_uris"] = app.RedirectUris,
            ["grant_types"] = app.AllowedGrantTypes,
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = OidcClientAuthMethods.None,
            ["scope"] = string.Join(' ', OidcAgentClients.Scopes),
        }, statusCode: StatusCodes.Status201Created);
    }

    private static IResult Invalid(string error, string description)
        => Results.Json(new { error, error_description = description }, statusCode: StatusCodes.Status400BadRequest);
}
