namespace MintPlayer.Spark.IdentityProvider.Configuration;

/// <summary>
/// The grants the provider issues tokens by (<see cref="SparkIdentityProviderOptions.Flows"/>,
/// <c>Spark:IdentityProvider:Flows</c>). Default <see cref="All"/>; a flow left out is unmapped where it has endpoints of
/// its own, refused at <c>/connect/token</c> (<c>unsupported_grant_type</c>) and absent from the metadata documents.
/// </summary>
/// <remarks>
/// <c>/connect/token</c>, <c>/.well-known/openid-configuration</c> and <c>/.well-known/jwks</c> are mapped with any flow.
/// Which flows depend on which, and the startup error when they do not fit, are in the package README ("Choosing flows and
/// endpoints").
/// </remarks>
[Flags]
public enum SparkIdentityProviderFlows
{
    /// <summary>No flow; refused at startup, because the provider would issue no tokens.</summary>
    None = 0,

    /// <summary>
    /// The authorization code flow with PKCE: <c>/connect/authorize</c>, the sign-in, two-factor, consent and error pages,
    /// and the <c>authorization_code</c> grant. AI agents sign in with it (<see cref="SparkIdentityProviderOptions.ForMcp"/>).
    /// </summary>
    AuthorizationCode = 1,

    /// <summary>
    /// The <c>refresh_token</c> grant, rotating, a reused token revoking its family. Needs <see cref="AuthorizationCode"/> or
    /// <see cref="DeviceCode"/>, the flows that issue refresh tokens; without it none is issued.
    /// </summary>
    RefreshToken = 2,

    /// <summary>The <c>client_credentials</c> grant: machine clients acting as themselves.</summary>
    ClientCredentials = 4,

    /// <summary>The device grant (RFC 8628): <c>/connect/device_authorization</c>, the user-code page <c>/connect/device</c>, and the grant.</summary>
    DeviceCode = 8,

    /// <summary>Token exchange (RFC 8693), delegation and impersonation, allowed per application.</summary>
    TokenExchange = 16,

    /// <summary>Every flow: the default.</summary>
    All = AuthorizationCode | RefreshToken | ClientCredentials | DeviceCode | TokenExchange,
}

/// <summary>
/// The endpoints beyond the flows' own (<see cref="SparkIdentityProviderOptions.Endpoints"/>,
/// <c>Spark:IdentityProvider:Endpoints</c>). Default <see cref="All"/>; an endpoint left out is not mapped (404) and the
/// metadata documents do not advertise it.
/// </summary>
/// <remarks>
/// Anonymous registration of AI agents at <c>/connect/register</c> is not one of these: it is the agent server's own switch
/// (<see cref="SparkIdentityProviderMcpOptions.DynamicRegistration"/>), and maps <c>POST /connect/register</c> by itself.
/// </remarks>
[Flags]
public enum SparkIdentityProviderEndpoints
{
    /// <summary>Only the flows' own endpoints, the discovery document and the JWKS.</summary>
    None = 0,

    /// <summary><c>/connect/userinfo</c>, GET and POST (OpenID Connect Core §5.3). Needs a flow that signs a user in.</summary>
    UserInfo = 1,

    /// <summary>
    /// <c>/connect/logout</c>: RP-initiated logout with back- and front-channel logout, and the consent pages' "Not you?" link.
    /// Needs <see cref="SparkIdentityProviderFlows.AuthorizationCode"/>.
    /// </summary>
    EndSession = 2,

    /// <summary><c>/connect/introspect</c> (RFC 7662), for resource servers that ask about every token.</summary>
    Introspection = 4,

    /// <summary><c>/connect/revoke</c> (RFC 7009).</summary>
    Revocation = 8,

    /// <summary><c>/connect/par</c>, pushed authorization requests (RFC 9126). Needs <see cref="SparkIdentityProviderFlows.AuthorizationCode"/>.</summary>
    PushedAuthorization = 16,

    /// <summary>
    /// Dynamic registration by developers: <c>POST /connect/register</c> with an initial access token (RFC 7591),
    /// <c>/connect/register/{clientId}</c> (RFC 7592), and the portal's <c>POST /spark/identity-provider/developer/registration-token</c>
    /// that issues those tokens. Needs <see cref="DeveloperPortal"/>.
    /// </summary>
    DeveloperRegistration = 32,

    /// <summary>The developer portal's API: developer status and requests, team invitations (<c>/spark/identity-provider/*</c>).</summary>
    DeveloperPortal = 64,

    /// <summary>
    /// The user's connected applications at the provider: the server-rendered <c>/connect/applications</c> and the SPA's
    /// <c>/spark/identity-provider/applications</c>. Needs a flow that signs a user in.
    /// </summary>
    ConnectedApplications = 128,

    /// <summary>Signing-key administration: <c>/spark/identity-provider/admin/keys</c> and its manual rotation.</summary>
    Administration = 256,

    /// <summary>Every endpoint: the default.</summary>
    All = UserInfo | EndSession | Introspection | Revocation | PushedAuthorization | DeveloperRegistration | DeveloperPortal
        | ConnectedApplications | Administration,
}
