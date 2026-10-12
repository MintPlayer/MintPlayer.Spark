using System.Security.Claims;

namespace MintPlayer.Spark.Mcp;

/// <summary>How the calling agent obtained its token (MCP PRD D7). Trustworthy: it is decided by the server.</summary>
public enum EAgentKind
{
    /// <summary>No token: the call runs as <c>anonymous</c> and is always read-only.</summary>
    Anonymous,

    /// <summary>A personal access token the user created on their account page.</summary>
    PersonalAccessToken,

    /// <summary>A token from an external authorization server, mapped through a linked external login.</summary>
    ExternalAuthorizationServer,

    /// <summary>An embedded-server client identified by a Client ID Metadata Document fetched over HTTPS.</summary>
    MetadataDocument,

    /// <summary>An embedded-server client that registered itself dynamically (loopback redirects only). Unverified.</summary>
    DynamicRegistration,

    /// <summary>An embedded-server client an administrator registered.</summary>
    PreRegistered,
}

/// <summary>
/// What <c>mcp.AgentPermissions</c> is told about the caller (MCP PRD D7). Only what can be trusted is
/// named plainly; a client's self-declared name is <see cref="UnverifiedName"/>.
/// </summary>
/// <param name="Kind">How the agent obtained its token.</param>
/// <param name="ClientId">The OAuth client id (or token id) of the registration. An identity of that registration only.</param>
/// <param name="VerifiedHost">Set only for <see cref="EAgentKind.MetadataDocument"/> clients, whose document was fetched over HTTPS from this host.</param>
/// <param name="UnverifiedName">The name the client calls itself. <b>Never trust it</b>: any program can call itself "Claude Code".</param>
/// <param name="User">The principal the call runs as: the signed-in user, or an unauthenticated principal for <see cref="EAgentKind.Anonymous"/>.</param>
public sealed record AgentInformation(
    EAgentKind Kind,
    string? ClientId,
    string? VerifiedHost,
    string? UnverifiedName,
    ClaimsPrincipal User)
{
    /// <summary>The anonymous caller: no token on the request.</summary>
    public static AgentInformation Anonymous(ClaimsPrincipal user) => new(EAgentKind.Anonymous, null, null, null, user);
}
