namespace MintPlayer.Spark.Mcp;

/// <summary>
/// How agents may obtain a token for <c>/mcp</c> (MCP PRD D4, §4.2). Flags: the effective set is the
/// configured flags intersected with what the application has actually wired up, and a flag set
/// explicitly without its wiring refuses startup.
/// </summary>
[Flags]
public enum EAgentAuthentication
{
    /// <summary>No agent sign-in at all: every call runs as <c>anonymous</c>.</summary>
    None = 0,

    /// <summary>Setup 1: a <c>spat_…</c> token the user creates on their account page (Authorization package).</summary>
    PersonalAccessToken = 1,

    /// <summary>Setup 2: tokens from the organization's OAuth server, mapped through a linked external login.</summary>
    ExternalAuthorizationServer = 2,

    /// <summary>Setup 3: the application is the OAuth server (IdentityProvider package, <c>ForMcp()</c>).</summary>
    EmbeddedAuthorizationServer = 4,

    /// <summary>Every setup the application has wired up. The default.</summary>
    All = PersonalAccessToken | ExternalAuthorizationServer | EmbeddedAuthorizationServer,
}
