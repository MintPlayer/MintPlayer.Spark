using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.IdentityProvider.Configuration;

public class SparkIdentityProviderOptions
{
    /// <summary>
    /// The issuer identifier this provider stamps on every token it mints and requires on
    /// every token it validates — e.g. <c>https://id.example.com</c>.
    /// <para>
    /// Required outside Development. It was previously derived from the request's
    /// <c>Host</c> header on every issuance and validation path, which the client controls: a
    /// forged <c>Host</c> minted tokens claiming a different issuer, signed with the real key,
    /// and a relying party that trusts this key would accept them. In Development it still
    /// falls back to the request so a local run needs no configuration.
    /// </para>
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// Path to signing key file. Default: oidc-signing-key.json in the application's <see cref="SparkAppData"/> directory.
    /// Auto-generated in Development; must be provided in Production.
    /// </summary>
    public string SigningKeyPath { get; set; } = SparkAppData.Relative("oidc-signing-key.json");

    /// <summary>
    /// Whether to auto-approve consent for clients with ConsentType = "implicit".
    /// </summary>
    public bool AutoApproveImplicitConsent { get; set; } = true;

    /// <summary>Who may become a developer, and on what terms (<c>Spark:IdentityProvider:Developers</c>, PRD D2).</summary>
    public SparkIdentityProviderDeveloperOptions Developers { get; set; } = new();

    /// <summary>How applications go live (<c>Spark:IdentityProvider:Apps</c>, PRD D4).</summary>
    public SparkIdentityProviderAppOptions Apps { get; set; } = new();

    /// <summary>The second factor at the provider's own sign-in (<c>Spark:IdentityProvider:TwoFactor</c>, #490 PRD D11).</summary>
    public SparkIdentityProviderTwoFactorOptions TwoFactor { get; set; } = new();

    /// <summary>How the <c>/connect/*</c> pages present the provider (<c>Spark:IdentityProvider:Branding</c>, PRD D7).</summary>
    public SparkIdentityProviderBranding Branding { get; set; } = new();

    /// <summary>
    /// The application's registration page (<c>Spark:IdentityProvider:RegistrationPath</c>), linked from <c>/connect/login</c>
    /// as "Create an account" with the pending authorization as <c>returnUrl</c>, when the application offers registration
    /// (<c>SparkLocalCredentials.Full</c>). Default <c>/register</c>, ng-spark's <c>withRegistration()</c> route; empty hides the link.
    /// </summary>
    public string? RegistrationPath { get; set; } = "/register";

    /// <summary>The signing-key rotation schedule (<c>Spark:IdentityProvider:Keys</c>, PRD D8).</summary>
    public SparkIdentityProviderKeyOptions Keys { get; set; } = new();

    /// <summary>The audit trail (<c>Spark:IdentityProvider:Audit</c>, PRD D9).</summary>
    public SparkIdentityProviderAuditOptions Audit { get; set; } = new();

    /// <summary>The machine-endpoint policy and the client-authentication throttle (<c>Spark:IdentityProvider:RateLimits</c>, PRD D9).</summary>
    public SparkIdentityProviderRateLimitOptions RateLimits { get; set; } = new();

    /// <summary>
    /// The authorization server for AI agents (MCP PRD D4), <see langword="null"/> when off. Turned on by
    /// <see cref="ForMcp"/> (this application's own MCP endpoint, setup 3, needs <c>spark.AddMcp(...)</c>), by
    /// <see cref="ForMcpResource"/> (another application's MCP endpoint, that application's setup 2), or by a
    /// <c>Spark:IdentityProvider:Mcp</c> section.
    /// </summary>
    public SparkIdentityProviderMcpOptions? Mcp { get; set; }

    /// <summary>
    /// Lets AI agents sign users in for the application's MCP endpoint (MCP PRD D4 setup 3, D5): RFC 8414 metadata,
    /// Client ID Metadata Documents, anonymous loopback registration, the <c>mcp:read</c>/<c>mcp:write</c> scopes with
    /// the MCP canonical URL as audience, refresh rotation, and the agent consent screen.
    /// </summary>
    public SparkIdentityProviderOptions ForMcp(Action<SparkIdentityProviderMcpOptions>? configure = null)
    {
        Mcp ??= new SparkIdentityProviderMcpOptions();
        Mcp.LocalEndpoint = true;
        configure?.Invoke(Mcp);
        return this;
    }

    /// <summary>
    /// Lets AI agents sign users in here for <b>another</b> application's MCP endpoint, which accepts this server's tokens
    /// as its external authorization server (that application's <c>AddExternalAgentAuthorizationServer</c>, MCP PRD D22).
    /// The same agent server as <see cref="ForMcp"/> (RFC 8414 metadata, metadata documents, loopback registration,
    /// refresh rotation, the consent screen), with an API resource whose audience is <paramref name="canonicalUrl"/> and
    /// the scopes <c>mcp:read</c> and <c>mcp:write</c>. Call once per endpoint; needs no MCP endpoint in this application.
    /// </summary>
    /// <param name="canonicalUrl">That endpoint's canonical URL exactly as it publishes it (its <c>Spark:Mcp:CanonicalUrl</c>).</param>
    /// <remarks>
    /// The consent screen cannot see what that application exposes, so it offers read or read + changes only; which
    /// types the agent may use, and whether changes are on, the user decides in that application (its Connected
    /// applications page), where the agent's grant lives.
    /// </remarks>
    public SparkIdentityProviderOptions ForMcpResource(string canonicalUrl, Action<SparkIdentityProviderMcpResource>? configure = null)
    {
        Mcp ??= new SparkIdentityProviderMcpOptions();
        var resource = new SparkIdentityProviderMcpResource { Url = canonicalUrl };
        configure?.Invoke(resource);
        Mcp.Resources.Add(resource);
        return this;
    }

    /// <summary>
    /// The grants the provider issues tokens by (<c>Spark:IdentityProvider:Flows</c>, e.g. <c>"AuthorizationCode, RefreshToken"</c>).
    /// Default <see cref="SparkIdentityProviderFlows.All"/>. A flow left out is not mapped, is refused at the token endpoint and
    /// is not advertised. Checked at startup against <see cref="Endpoints"/> and the agent server: a combination that cannot
    /// work refuses to start, naming the dependency (README, "Choosing flows and endpoints").
    /// </summary>
    public SparkIdentityProviderFlows Flows { get; set; } = SparkIdentityProviderFlows.All;

    /// <summary>
    /// The endpoints beyond the flows' own (<c>Spark:IdentityProvider:Endpoints</c>, e.g. <c>"Revocation, UserInfo"</c>).
    /// Default <see cref="SparkIdentityProviderEndpoints.All"/>. An endpoint left out is not mapped (404) and not advertised.
    /// </summary>
    public SparkIdentityProviderEndpoints Endpoints { get; set; } = SparkIdentityProviderEndpoints.All;

    /// <summary>
    /// Maps only what AI agents need, for an application that hosts the provider just to sign agents in to its own MCP
    /// endpoint (<see cref="ForMcp"/>): the authorization code flow (sign-in, consent) with refresh tokens, revocation, the
    /// discovery documents and JWKS, and the agent server's Client ID Metadata Documents and anonymous loopback registration.
    /// The device grant, client credentials, token exchange, PAR, introspection, userinfo, end-session, developer
    /// registration and portal, the provider's connected-applications pages and key administration are not mapped.
    /// </summary>
    /// <remarks>
    /// It sets <see cref="Flows"/> and <see cref="Endpoints"/>; a later assignment wins. Users manage their agents on the
    /// application's account page (Connected applications), not at the provider. Not for a provider that also signs agents in
    /// for another application's endpoint (<see cref="ForMcpResource"/>): that application's users sign in here to link their
    /// login, and its sign-in reads userinfo, so startup refuses the combination.
    /// </remarks>
    public SparkIdentityProviderOptions AgentsOnly()
    {
        Flows = SparkIdentityProviderFlows.AuthorizationCode | SparkIdentityProviderFlows.RefreshToken;
        Endpoints = SparkIdentityProviderEndpoints.Revocation;
        return this;
    }

    /// <summary>
    /// Opt in to cross-origin access on the OIDC protocol endpoints. <b>Off by default.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a <b>browser-based client on a different origin</b> needs this — a SPA doing
    /// authorization-code + PKCE against this provider, hosted somewhere else. An application's own
    /// Angular frontend does not: Spark serves it from the same host, so every call to
    /// <c>/connect/token</c> is same-origin and CORS never enters into it.
    /// </para>
    /// <para>
    /// ⚠️ <b>This said it "automatically allows origins registered in
    /// <c>OidcApplication.AllowedCorsOrigins</c>", defaulted to <see langword="true"/>, and did neither
    /// of those things.</b> The policy was <c>SetIsOriginAllowed(_ =&gt; true)</c> — any origin, never
    /// consulting the registered list, which was read by nothing in this repository. So every
    /// application that turned the identity provider on granted a cross-origin permission it did not
    /// ask for, to support a scenario whose access control was never built, described by a comment that
    /// said the opposite.
    /// </para>
    /// <para>
    /// It now does what it always claimed: the allowed set is the union of
    /// <c>AllowedCorsOrigins</c> across <b>enabled</b> applications. A union, because a preflight is an
    /// anonymous <c>OPTIONS</c> with no <c>client_id</c> — see <c>OidcCorsOrigins</c>.
    /// </para>
    /// <para>
    /// ⚠️ <b>Turning this on without registering an origin allows nothing.</b> The symptom is a
    /// missing header rather than an error, so the host logs a warning at startup when that is the
    /// case. See <c>docs/guide-cors.md</c>.
    /// </para>
    /// </remarks>
    public bool EnableDynamicCors { get; set; }
}
