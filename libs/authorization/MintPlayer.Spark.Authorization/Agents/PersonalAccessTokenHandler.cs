using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Authorization.Agents;

/// <summary>
/// Validates <c>Authorization: Bearer spat_…</c> (MCP PRD D11). Run only by the Mcp package on <c>/mcp</c>: the
/// scheme is not one of Spark's web credentials, so a personal access token opens nothing else.
/// </summary>
/// <remarks>
/// A token is accepted only when its grant exists (revocation is deletion), the hash matches, it has not expired,
/// its user exists and is not locked out, and the user's security stamp is the one the token was created under
/// (a password change or a removed sign-in method revokes every token). The principal carries the local user id
/// as <c>sub</c> and as <see cref="ClaimTypes.NameIdentifier"/> (what application code reads, as from a cookie), and
/// the grant id, nothing else: groups come from the store per request (§4.6), never the token.
/// A refusal never says which check failed, and nothing here logs the token.
/// </remarks>
internal sealed partial class PersonalAccessTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    AgentGrantStore grants,
    AgentRequestUser users,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix + PersonalAccessTokens.Prefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult();

        var token = header[BearerPrefix.Length..].Trim();
        if (!PersonalAccessTokens.TryParse(token, out var userId, out var tokenId))
            return Refuse("malformed");

        var grant = await grants.GetAsync(AgentGrantIds.PersonalAccessToken(userId, tokenId), Context.RequestAborted);
        if (grant is null || grant.Kind != EAgentKind.PersonalAccessToken
            || !string.Equals(grant.UserId, userId, StringComparison.OrdinalIgnoreCase)
            || !PersonalAccessTokens.Matches(token, grant.TokenHash))
        {
            return Refuse("unknown or revoked");
        }

        var now = time.GetUtcNow();
        if (grant.ExpiresAt is not { } expires || expires <= now)
            return Refuse("expired", grant.Id);

        var user = await users.GetAsync(grant.UserId, Context.RequestAborted);
        if (user is null)
            return Refuse("user deleted", grant.Id);
        if (!string.Equals(user.SecurityStamp ?? string.Empty, grant.SecurityStamp ?? string.Empty, StringComparison.Ordinal))
            return Refuse("security stamp changed", grant.Id);
        if (AgentRequestUser.IsLockedOut(user, now))
            return Refuse("user locked out", grant.Id);

        await grants.TouchAsync(grant, Context.RequestAborted);

        var identity = new ClaimsIdentity(
            [
                new Claim("sub", grant.UserId),
                // The local id under the name application code reads too (Actions, row policies, quotas), as a cookie carries it.
                new Claim(ClaimTypes.NameIdentifier, grant.UserId),
                new Claim("name", user.UserName ?? grant.UserId),
                new Claim(PersonalAccessTokens.GrantClaim, grant.Id!),
            ],
            Scheme.Name, "name", "role");
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private AuthenticateResult Refuse(string reason, string? grantId = null)
    {
        LogRefused(Logger, reason, grantId ?? "-");
        return AuthenticateResult.Fail("The personal access token is not valid.");
    }

    [LoggerMessage(EventId = 7310, EventName = "SparkPersonalAccessTokenRefused", Level = LogLevel.Information,
        Message = "Personal access token refused ({Reason}), grant {GrantId}.")]
    private static partial void LogRefused(ILogger logger, string reason, string grantId);
}
