using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Authorization.Agents;

/// <summary>
/// The groups of a caller authenticated by a local-subject bearer scheme — an agent token on <c>/mcp</c>
/// (personal access tokens, the embedded server's tokens) — read from the user store on every request (MCP PRD
/// D4, §4.6, spike S4). Tokens carry no groups by design, so without this a signed-in agent had only the
/// <c>authenticated</c> group; with it, removing the user from a group denies the token's next call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bearer principals only.</b> Composition is a union, so this provider cannot take a stale cookie claim away;
/// it answers nothing for a request authenticated by an ambient credential or by a scheme whose <c>sub</c> is
/// not the local user id. The cookie path is unchanged: its role claims refresh at Identity's security-stamp
/// validation (30 minutes by default), so after a membership change the agent is the stricter side.
/// </para>
/// <para>
/// The same groups the cookie would carry: <see cref="Identity.SparkUser.Roles"/> and the user's stored claims
/// of a group type (the claim types core's claims provider reads). Cached per request only (core asks once).
/// </para>
/// </remarks>
internal sealed class AgentStoreGroupMembership(IHttpContextAccessor accessor, ISparkCurrentUser currentUser, AgentRequestUser users)
    : IGroupMembershipProvider
{
    /// <summary>The claim types core's <c>ClaimsGroupMembershipProvider</c> reads groups from.</summary>
    private static readonly string[] GroupClaimTypes =
    [
        "group",
        "groups",
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/groupsid",
        "http://schemas.xmlsoap.org/claims/Group",
    ];

    public async Task<IEnumerable<string>> GetCurrentUserGroupsAsync(CancellationToken cancellationToken = default)
    {
        var scheme = accessor.HttpContext?.Features.Get<ISparkAuthenticatedSchemeFeature>()?.Scheme;
        if (scheme is not { SubjectIsLocalUserId: true, IsAmbient: false } || currentUser.Id is not { Length: > 0 } userId)
            return [];

        var user = await users.GetAsync(userId, cancellationToken);
        if (user is null)
            return [];

        return user.Roles
            .Concat(user.Claims
                .Where(c => c.ClaimType is not null && GroupClaimTypes.Contains(c.ClaimType, StringComparer.OrdinalIgnoreCase))
                .Select(c => c.ClaimValue))
            .OfType<string>()
            .Where(g => g.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
