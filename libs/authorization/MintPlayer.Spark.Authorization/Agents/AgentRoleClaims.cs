using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Agents;

/// <summary>The role and group claims of a user's sign-in cookie, and the claim type <c>User.IsInRole</c> reads them by.</summary>
/// <param name="RoleClaimType">The cookie identity's role claim type (Identity's <c>ClaimsIdentity.RoleClaimType</c>).</param>
/// <param name="Claims">The role claims and the group claims (the claim types core reads groups from), nothing else.</param>
internal sealed record AgentRoleClaims(string RoleClaimType, IReadOnlyList<Claim> Claims);

/// <summary>
/// The role and group claims a cookie sign-in of a user carries, for the principal of an agent request (MCP PRD §4.6):
/// application code that tests <c>User.IsInRole</c> or reads role claims (Fleet's car row filter) treats an agent as it
/// treats the user's cookie. Generic over the user type behind it, like <see cref="IAgentLinkedLogins"/>.
/// </summary>
internal interface IAgentRoleClaimsProvider
{
    /// <summary>The claims for <paramref name="user"/>, made now from the stored user: nothing caches them.</summary>
    Task<AgentRoleClaims> GetAsync(SparkUser user, CancellationToken cancellationToken = default);
}

/// <summary>
/// Identity's own claims factory (<see cref="IUserClaimsPrincipalFactory{TUser}"/>, the one that fills the sign-in cookie:
/// the user's roles, the claims of those roles, the claims stored on the user), filtered to role and group claims.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per request (S4).</b> Each agent scheme calls it once per request with the user it loaded for the token check
/// (<see cref="AgentRequestUser"/>), so a role removed from the user is gone on the agent's next call. The cookie keeps its
/// claims until Identity's security-stamp validation renews it (30 minutes by default): after a role change the agent is
/// the stricter side, as it is for groups (<see cref="AgentStoreGroupMembership"/>).
/// </para>
/// <para>
/// <b>Role and group claims only.</b> The cookie's other claims (email, security stamp, whatever is stored on the user)
/// stay off the agent's principal: a stored claim named like a scheme's own (<c>scope</c>, <c>client_id</c>, a grant claim)
/// would sit beside it, and <c>scope</c> is setup 2's write cap (D22).
/// </para>
/// </remarks>
internal sealed class AgentRoleClaimsProvider<TUser>(
    UserManager<TUser> users,
    IUserClaimsPrincipalFactory<TUser> factory,
    IOptions<IdentityOptions> identityOptions) : IAgentRoleClaimsProvider
    where TUser : SparkUser
{
    public async Task<AgentRoleClaims> GetAsync(SparkUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The scheme's own read when the application's user type is SparkUser; a subtype is loaded as itself, since its
        // claims factory may read its own members.
        var typed = user as TUser ?? (user.Id is { Length: > 0 } id ? await users.FindByIdAsync(id) : null);
        var cookie = typed is null ? null : (await factory.CreateAsync(typed)).Identity as ClaimsIdentity;
        var roleClaimType = cookie?.RoleClaimType ?? identityOptions.Value.ClaimsIdentity.RoleClaimType;
        if (cookie is null)
            return new AgentRoleClaims(roleClaimType, []);

        return new AgentRoleClaims(
            roleClaimType,
            cookie.Claims
                .Where(c => string.Equals(c.Type, roleClaimType, StringComparison.Ordinal)
                            || AgentStoreGroupMembership.GroupClaimTypes.Contains(c.Type, StringComparer.OrdinalIgnoreCase))
                .Select(c => new Claim(c.Type, c.Value))
                .ToList());
    }
}
