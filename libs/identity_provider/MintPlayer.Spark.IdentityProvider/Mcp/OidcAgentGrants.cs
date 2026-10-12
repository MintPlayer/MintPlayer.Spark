using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Agents;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Mcp;

/// <summary>
/// The embedded server's side of the agent grant (MCP PRD D12): the consent screen writes
/// <c>AgentGrants/{userId}/app/{clientId}</c> linked to the <c>OidcGrant</c>, and revoking either revokes both. The
/// agent grant is the source of truth for what the agent may do; the <c>OidcGrant</c> keeps the OAuth bookkeeping
/// (scopes, token chain).
/// </summary>
internal sealed class OidcAgentGrants(AgentGrantStore grants, IDocumentStore store, IServiceProvider services) : IAgentGrantLink
{
    /// <summary>Writes (or replaces) the user's grant for this agent client with what they chose on the consent screen.</summary>
    public async Task<AgentGrant> GrantAsync(
        string userId, string? securityStamp, OidcApplication app, string oidcGrantId,
        EAgentPermission permissions, List<AgentGrantType>? types, CancellationToken ct)
    {
        var grant = new AgentGrant
        {
            Id = AgentGrantIds.Application(userId, app.ClientId),
            UserId = userId,
            Kind = OidcAgentClients.KindOf(app),
            ClientId = app.ClientId,
            Name = app.DisplayName,
            VerifiedHost = OidcAgentClients.VerifiedHostOf(app),
            Permissions = permissions,
            Types = types,
            CreatedAt = DateTimeOffset.UtcNow,
            SecurityStamp = securityStamp ?? string.Empty,
            OidcGrantId = oidcGrantId,
        };
        if (await grants.GetOwnAsync(userId, grant.Id, ct) is { } previous)
        {
            grant.CreatedAt = previous.CreatedAt;
            grant.LastUsedAt = previous.LastUsedAt;
        }
        await grants.SaveAsync(grant, ct);
        return grant;
    }

    /// <summary>The OAuth grant was withdrawn (or its client removed): the agent grant goes with it.</summary>
    public async Task RemoveAsync(string userId, string clientId, CancellationToken ct)
        => await grants.DeleteAsync(userId, AgentGrantIds.Application(userId, clientId), ct);

    /// <summary>The user revoked the agent grant on Connected applications: withdraw the linked OAuth grant and its tokens.</summary>
    public async Task RevokedAsync(AgentGrant grant, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(grant.OidcGrantId))
            return;

        string? applicationId;
        using (var session = store.OpenAsyncSession())
            applicationId = (await session.LoadAsync<OidcGrant>(grant.OidcGrantId, cancellationToken))?.ApplicationId;
        if (applicationId is null)
            return;

        // Resolved lazily: the withdrawal service removes agent grants through this class in turn.
        var withdrawal = (OidcGrantWithdrawal)services.GetService(typeof(OidcGrantWithdrawal))!;
        await withdrawal.WithdrawAsync(grant.UserId, applicationId, scopes: null, ipAddress: null, cancellationToken);
    }
}
