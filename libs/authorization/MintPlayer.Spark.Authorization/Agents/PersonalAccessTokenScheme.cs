using System.Security.Claims;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Authorization.Agents;

/// <summary>
/// Setup 1 for the Mcp package (MCP PRD D4): personal access tokens are wired whenever this package is, and an
/// application that also adds the Mcp package accepts them on <c>/mcp</c> unless <c>mcp.Authentication</c> leaves
/// the flag out. The token's <c>sub</c> is the local user id.
/// </summary>
internal sealed class PersonalAccessTokenScheme(AgentGrantStore grants) : IAgentCredentialScheme
{
    public EAgentAuthentication Setup => EAgentAuthentication.PersonalAccessToken;

    public string Scheme => PersonalAccessTokens.Scheme;

    public bool SubjectIsLocalUserId => true;

    public async ValueTask<AgentCredential?> DescribeAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (principal.FindFirst(PersonalAccessTokens.GrantClaim)?.Value is not { Length: > 0 } grantId)
            return null;

        var grant = await grants.GetAsync(grantId, cancellationToken);
        if (grant is null)
            return null;

        var agent = new AgentInformation(EAgentKind.PersonalAccessToken, grant.ClientId, VerifiedHost: null, grant.Name, principal)
        {
            GrantId = grant.Id,
        };
        return new AgentCredential(agent, grant.ToScope());
    }
}
