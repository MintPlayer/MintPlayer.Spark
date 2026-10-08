using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Provider sessions and logout (OIDC Back-Channel Logout 1.0, Front-Channel Logout 1.0,
/// <c>docs/identity_provider_platform_PRD.md</c> D8, I10).
/// <list type="bullet">
/// <item>A sign-in at the provider gets a session id (<c>sid</c>), carried in its cookie and in every
/// token issued under it. The session is an <see cref="OidcToken"/> of type <see cref="OidcTokenTypes.Session"/>
/// listing the clients that received tokens in it.</item>
/// <item>Ending the session revokes its refresh tokens (D8: "Logout revokes the session's refresh tokens"),
/// posts a logout token to every client with a back-channel logout URI, and returns the front-channel
/// URIs the logout page loads in iframes. Front-channel is best effort: browsers that block third-party
/// cookies can stop it from reaching the client's session.</item>
/// </list>
/// </summary>
internal sealed class OidcSessionStore(IDocumentStore store, OidcKeyRing keys, IHttpClientFactory httpClientFactory, ILogger<OidcSessionStore> logger)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private const string BackChannelEvent = "http://schemas.openid.net/event/backchannel-logout";

    public static string DocumentId(string sid) => OidcTokenReference.DocumentId("session:" + sid);

    /// <summary>A new session id, for the sign-in cookie.</summary>
    public static string NewSessionId() => OidcTokenReference.GenerateValue();

    /// <summary>Records that <paramref name="applicationId"/> received tokens in session <paramref name="sid"/>.</summary>
    public static async Task JoinAsync(IAsyncDocumentSession session, string? sid, string userId, string applicationId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sid))
            return;
        var record = await session.LoadAsync<OidcToken>(DocumentId(sid), ct);
        if (record is null)
        {
            record = new OidcToken
            {
                Id = DocumentId(sid),
                Type = OidcTokenTypes.Session,
                Subject = userId,
                SessionId = sid,
                Status = "valid",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(Lifetime),
            };
            await session.StoreExpiringAsync(record, ct);
        }
        var clients = (record.Properties.GetValueOrDefault("clients") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (clients.Add(applicationId))
            record.Properties["clients"] = string.Join(' ', clients);
    }

    /// <summary>The front-channel logout URIs the logout page loads, each with <c>iss</c> and <c>sid</c> when the client asked for them.</summary>
    public sealed record Ended(IReadOnlyList<string> FrontChannelUris);

    /// <summary>Ends session <paramref name="sid"/>: refresh tokens revoked, back-channel logout tokens sent.</summary>
    public async Task<Ended> EndAsync(string? sid, string issuer, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sid))
            return new([]);

        using var session = store.OpenAsyncSession();
        var record = await session.LoadAsync<OidcToken>(DocumentId(sid), ct);
        if (record is not { Type: OidcTokenTypes.Session, Status: "valid" })
            return new([]);
        record.Status = "revoked";

        // An index read: a refresh token minted in the last moments may be missed. Refresh then
        // re-checks the grant and the session-bound state, so this is cleanup, not the enforcement.
        var refreshTokens = await session.Query<OidcToken>()
            .Where(t => t.SessionId == sid && t.Type == OidcTokenTypes.RefreshToken && t.Status == "valid")
            .ToListAsync(ct);
        foreach (var token in refreshTokens)
            token.Status = "revoked";
        await session.SaveChangesAsync(ct);

        var appIds = (record.Properties.GetValueOrDefault("clients") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var apps = await session.LoadAsync<OidcApplication>(appIds, ct);
        var frontChannel = new List<string>();
        foreach (var app in apps.Values.OfType<OidcApplication>())
        {
            if (!string.IsNullOrEmpty(app.BackChannelLogoutUri))
                await SendBackChannelAsync(app, record.Subject, sid, issuer, ct);
            if (!string.IsNullOrEmpty(app.FrontChannelLogoutUri))
                frontChannel.Add(app.FrontChannelLogoutSessionRequired
                    ? Endpoints.RedirectUrl.With(app.FrontChannelLogoutUri, ("iss", issuer), ("sid", sid))
                    : app.FrontChannelLogoutUri);
        }
        return new(frontChannel);
    }

    /// <summary>Back-Channel Logout 1.0 §2.4: a signed logout token, POSTed as <c>logout_token</c>. Failures are logged, not retried.</summary>
    private async Task SendBackChannelAsync(OidcApplication app, string userId, string sid, string issuer, CancellationToken ct)
    {
        var claims = new Dictionary<string, object>
        {
            ["jti"] = OidcTokenReference.GenerateValue(),
            ["events"] = new Dictionary<string, object> { [BackChannelEvent] = new Dictionary<string, object>() },
            ["sub"] = OidcSubjects.For(app, userId),
            // Always sent, not only when BackChannelLogoutSessionRequired: a client that ignores sid loses nothing.
            ["sid"] = sid,
        };

        var logoutToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            TokenType = "logout+jwt",
            Issuer = issuer,
            Audience = app.ClientId,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(2),
            Claims = claims,
            SigningCredentials = keys.GetSigningCredentials(app.IdTokenSignedResponseAlg),
        });

        try
        {
            using var client = httpClientFactory.CreateClient("Spark.IdentityProvider.BackChannelLogout");
            client.Timeout = TimeSpan.FromSeconds(5);
            using var response = await client.PostAsync(app.BackChannelLogoutUri,
                new FormUrlEncodedContent([KeyValuePair.Create("logout_token", logoutToken)]), ct);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Back-channel logout to {ClientId} answered {Status}.", app.ClientId, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Back-channel logout to {ClientId} failed.", app.ClientId);
        }
    }

    /// <summary>Gives a fresh provider sign-in its session id (cookie <c>OnSigningIn</c>).</summary>
    public static void StampSessionId(ClaimsPrincipal principal)
    {
        if (principal.Identity is ClaimsIdentity identity && identity.FindFirst(OidcSessions.SessionIdClaim) is null)
            identity.AddClaim(new Claim(OidcSessions.SessionIdClaim, NewSessionId()));
    }
}
