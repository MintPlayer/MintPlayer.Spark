using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Authorization.Agents;

/// <summary>
/// What one user allowed one AI agent (MCP PRD D12): the source of truth for an agent's access, read on every
/// <c>/mcp</c> call, so an edit or a revocation applies to the agent's next call without a new sign-in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural ids</b> (<see cref="AgentGrantIds"/>), one document per user and agent:
/// <c>AgentGrants/{userId}/pat/{tokenId}</c> for a personal access token,
/// <c>AgentGrants/{userId}/ext/{sha256(issuer|clientId)[..32]}</c> for an external authorization server's client,
/// <c>AgentGrants/{userId}/app/{clientId}</c> for an embedded-server client. The user id is part of the id, so a
/// user's grants are found without an index and the account endpoints never address another user's.
/// </para>
/// <para>
/// The grant is one layer of the effective permission, app(agent) ∩ type ∩ <b>grant</b> ∩ rights (D6): it narrows
/// what the user's own rights allow and never widens it.
/// </para>
/// </remarks>
public sealed class AgentGrant
{
    /// <summary>The natural id; see <see cref="AgentGrantIds"/>.</summary>
    public string? Id { get; set; }

    /// <summary>The user the agent acts as.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>How the agent obtained its token.</summary>
    public EAgentKind Kind { get; set; }

    /// <summary>The OAuth client id; for a personal access token, the token id.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// For a personal access token, the name its user gave it. For an OAuth client, the name the client calls
    /// itself, which proves nothing (shown as unverified).
    /// </summary>
    public string? Name { get; set; }

    /// <summary>For a metadata-document client only: the host its document was fetched from over HTTPS.</summary>
    public string? VerifiedHost { get; set; }

    /// <summary>Read, or read + changes (<see cref="EAgentPermission.Write"/>): the cap for every type.</summary>
    public EAgentPermission Permissions { get; set; } = EAgentPermission.Read;

    /// <summary>
    /// <see langword="null"/>: everything the application makes available to agents, including types exposed
    /// later. Otherwise the chosen types, each with its own permission (capped by <see cref="Permissions"/>).
    /// </summary>
    public List<AgentGrantType>? Types { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When a personal access token stops working (mandatory for tokens, D11).</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>The last call, written at most once a minute (D15).</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>A personal access token's SHA-256, hex. The token itself is never stored.</summary>
    public string? TokenHash { get; set; }

    /// <summary>
    /// The user's security stamp when the grant was made: a personal access token's creation (D11), the embedded
    /// server's consent (M3b), an external server's agent's first call or the user allowing it again (D22, owner
    /// 2026-10-12). A password change, a removed sign-in method or any other stamp change revokes a token, makes the
    /// embedded server's agent sign in again, and disconnects an external server's agent until the user allows it again.
    /// </summary>
    public string? SecurityStamp { get; set; }

    /// <summary>The embedded server's <c>OidcGrant</c> this grant is linked with (plan M3b); revoking either revokes both.</summary>
    public string? OidcGrantId { get; set; }

    /// <summary>For an external authorization server's client: the server's issuer (<c>iss</c>), shown on the account page.</summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// For an external authorization server's client: when the user disconnected it. The external server keeps issuing
    /// tokens and the grant would otherwise be created again on the next call, so a disconnected grant stays, refusing
    /// every call, until the user allows the agent again (an edit clears it and re-stamps <see cref="SecurityStamp"/>).
    /// MCP PRD D22.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>The grant as the permission layer applies it.</summary>
    public AgentGrantScope ToScope()
        => new(Id!, Permissions, Types?.GroupBy(t => t.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Permissions, StringComparer.Ordinal));
}

/// <summary>One chosen type of an <see cref="AgentGrant"/>.</summary>
public sealed class AgentGrantType
{
    /// <summary>The entity type name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Read, or read + changes, for this type.</summary>
    public EAgentPermission Permissions { get; set; } = EAgentPermission.Read;
}
