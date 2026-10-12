namespace MintPlayer.Spark.Authorization.Agents;

/// <summary>
/// Told when a user revokes an <see cref="AgentGrant"/> on the account page (MCP PRD D12). The IdentityProvider's
/// embedded server links each of its agent grants with an <c>OidcGrant</c> (<see cref="AgentGrant.OidcGrantId"/>) and
/// withdraws that one too, so the agent's refresh token dies with the grant. Internal: only the IdentityProvider
/// (InternalsVisibleTo) registers one.
/// </summary>
internal interface IAgentGrantLink
{
    /// <summary>The grant was deleted; revoke whatever is linked to it.</summary>
    Task RevokedAsync(AgentGrant grant, CancellationToken cancellationToken);
}
