using System.Security.Claims;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// A credential scheme whose tokens an AI agent presents on <c>/mcp</c> on a user's behalf (MCP PRD D4): the
/// seam between the package that issues and validates the token and the Mcp package that serves the agent.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a seam.</b> Personal access tokens and agent grants live in the Authorization package (D11, D12), the
/// embedded authorization server in the IdentityProvider (D5), and neither may reference the Mcp package, nor
/// it them. A package that authenticates agents registers one of these (singleton, as an enumerable service);
/// the Mcp package learns from the registrations which setups are wired (D4: effective = flags ∩ wired, an
/// explicit flag without a registration refuses startup), runs <see cref="Scheme"/> on <c>/mcp</c> only, and
/// asks <see cref="DescribeAsync"/> who the agent is and what the user granted it.
/// </para>
/// <para>
/// <b>Not a web credential.</b> Registering this does not add <see cref="Scheme"/> to Spark's composite
/// scheme: an agent token authenticates <c>/mcp</c> and nothing else, because only there do the agent layers
/// (the app's <c>AgentPermissions</c>, the type's <c>agentPermissions</c>, the grant) narrow its rights.
/// </para>
/// </remarks>
public interface IAgentCredentialScheme
{
    /// <summary>Which of the three setups this scheme is.</summary>
    EAgentAuthentication Setup { get; }

    /// <summary>The ASP.NET Core authentication scheme that validates the token.</summary>
    string Scheme { get; }

    /// <summary>
    /// Whether the principal's <c>sub</c> is the local user id (setups 1 and 3). The Mcp package then declares
    /// the request authenticated by a local-subject scheme, so <c>ISparkCurrentUser.Id</c> reads <c>sub</c> and
    /// the user's groups come from the user store (PRD §4.6).
    /// </summary>
    bool SubjectIsLocalUserId { get; }

    /// <summary>
    /// The agent behind a principal <see cref="Scheme"/> produced, and the user's grant for it, read now (D12:
    /// a grant edited or revoked on the server applies to the next call). <see langword="null"/> when the grant
    /// no longer exists: the call is then refused as an invalid token, never run as anonymous.
    /// </summary>
    ValueTask<AgentCredential?> DescribeAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);

    /// <summary>
    /// The issuer of the authorization server an agent signs in at (RFC 9728 <c>authorization_servers</c>), for the
    /// setups that offer OAuth (2 and 3); <see langword="null"/> for one that does not (personal access tokens).
    /// </summary>
    /// <param name="requestOrigin">
    /// The request's origin (<c>scheme://host</c>). Only a server whose issuer is not configured and that runs in
    /// Development may answer with it, as its own token endpoint does; everywhere else the configured issuer wins.
    /// </param>
    string? AuthorizationServer(string requestOrigin) => null;

    /// <summary>The scopes an agent may ask this server for (RFC 9728 <c>scopes_supported</c>, the challenge's <c>scope</c>).</summary>
    IReadOnlyList<string> ScopesSupported => [];
}

/// <summary>The agent behind an <c>/mcp</c> request and what its user granted it.</summary>
/// <param name="Agent">Who the agent is, as <c>mcp.AgentPermissions</c> sees it.</param>
/// <param name="Grant">The user's grant (D12).</param>
public sealed record AgentCredential(AgentInformation Agent, AgentGrantScope Grant);

/// <summary>
/// What a user granted one agent (MCP PRD D12): read or read + changes, for every type the application makes
/// available to agents or for a chosen list. One layer of the effective permission, app(agent) ∩ type ∩
/// <b>grant</b> ∩ rights (D6), applied inside <c>IPermissionService</c> (D13) — it narrows and never widens.
/// </summary>
/// <param name="Id">The grant's document id (<c>AgentGrants/{userId}/…</c>).</param>
/// <param name="Permissions">The grant's cap: <see cref="EAgentPermission.Read"/>, or read + <see cref="EAgentPermission.Write"/> ("changes").</param>
/// <param name="Types">
/// <see langword="null"/>: every type the application makes available to agents, including types exposed
/// later. Otherwise the chosen types by entity type id (<c>"D"</c>, D28), each with its own permission, capped by
/// <paramref name="Permissions"/>; a type not listed gets nothing.
/// </param>
public sealed record AgentGrantScope(string Id, EAgentPermission Permissions, IReadOnlyDictionary<string, EAgentPermission>? Types)
{
    /// <summary>What the grant allows on the entity type whose id is <paramref name="typeId"/> (<c>"D"</c>).</summary>
    public EAgentPermission For(string typeId)
        => Types is null
            ? Permissions
            : Types.TryGetValue(typeId, out var permission) ? permission & Permissions : EAgentPermission.None;
}

/// <summary>
/// What the application makes available to agents, for the pages where a user chooses what an agent may do
/// (the Authorization package's Connected applications, the IdentityProvider's consent screen). Implemented by
/// the Mcp package; absent without it, and then there is nothing to grant.
/// </summary>
public interface IAgentExposure
{
    /// <summary>The setups the Mcp package accepts tokens of (configured flags ∩ wired).</summary>
    EAgentAuthentication Setups { get; }

    /// <summary>
    /// The entity types exposed to agents (<c>agentPermissions</c> other than <c>None</c>) that the current
    /// caller may see on the web, each with its type layer. Asked on a web request: a user is offered only types
    /// whose existence they may already know.
    /// </summary>
    Task<IReadOnlyList<AgentExposedType>> ListTypesAsync(CancellationToken cancellationToken = default);

    /// <summary>The application layer for <paramref name="agent"/> (<c>mcp.AgentPermissions</c>; anonymous clamped to read).</summary>
    EAgentPermission AppPermissions(AgentInformation agent);

    /// <summary>
    /// The MCP endpoint's canonical URL (<c>Spark:Mcp:CanonicalUrl</c>, else the public base URL + the path): the
    /// resource agents ask a token for (RFC 8707) and the only audience <c>/mcp</c> accepts. <see langword="null"/>
    /// when neither is configured. Never derived from a request's <c>Host</c>.
    /// </summary>
    string? ResourceUrl { get; }
}

/// <summary>An entity type agents may use.</summary>
/// <param name="Id">The entity type's id (<c>"D"</c>), as grants store it (D28: never the name, which a rename changes).</param>
/// <param name="Name">The entity type's name in this application.</param>
/// <param name="Label">The type's label in the request's language, or the name.</param>
/// <param name="Permissions">The type's <c>agentPermissions</c>.</param>
/// <param name="Queries">The labels of the type's queries, shown under it ("with their queries underneath", PRD §4.6).</param>
public sealed record AgentExposedType(string Id, string Name, string Label, EAgentPermission Permissions, IReadOnlyList<string> Queries);
