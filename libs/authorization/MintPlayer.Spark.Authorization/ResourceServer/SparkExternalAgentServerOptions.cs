namespace MintPlayer.Spark.Authorization.ResourceServer;

/// <summary>
/// How agents' access tokens from an external authorization server (Entra, Keycloak, Auth0, another Spark identity
/// provider) are accepted on <c>/mcp</c>: setup 2 of the MCP PRD (D4, D22). See
/// <see cref="SparkExternalAgentServerExtensions.AddExternalAgentAuthorizationServer"/>.
/// </summary>
public sealed class SparkExternalAgentServerOptions
{
    /// <summary>
    /// The server's issuer, exactly as its tokens' <c>iss</c> and its metadata name it. Pinned: a token from any other
    /// issuer is refused. Also its discovery base (<c>{Issuer}/.well-known/openid-configuration</c>) and what the RFC 9728
    /// document lists in <c>authorization_servers</c>.
    /// </summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// The application's sign-in provider for the same server: the authentication scheme name users signed in to the
    /// website with (<c>spark.AddOpenIdConnect("Keycloak", …)</c> → <c>"Keycloak"</c>). A token maps to a local user only
    /// when that user has a login of this provider whose key equals the token's <see cref="SubjectClaim"/> (D22): never
    /// by email. Startup fails when no such scheme is registered.
    /// </summary>
    public string LoginProvider { get; set; } = string.Empty;

    /// <summary>
    /// The access-token claim that equals the linked login's key. <c>sub</c> by default. A server whose access-token
    /// <c>sub</c> differs from the one its sign-in gives the website (Entra's is per application) needs a claim both
    /// share (<c>oid</c>), with the website's sign-in storing that claim as the login key.
    /// </summary>
    public string SubjectClaim { get; set; } = "sub";

    /// <summary>Require HTTPS for the discovery document. Off only against a local http issuer.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Accept only tokens typed <c>at+jwt</c> (RFC 9068). On by default. An issuer that types its access tokens
    /// <c>JWT</c> needs it off; an ID token is still refused then, because its audience is the client, never the MCP
    /// endpoint.
    /// </summary>
    public bool RequireAccessTokenType { get; set; } = true;

    /// <summary>
    /// Validate tokens by asking the issuer (RFC 7662) instead of checking a JWT locally: required for an issuer that
    /// hands out opaque tokens, and a revoked token stops working at once. Needs <see cref="IntrospectionClientId"/> and
    /// <see cref="IntrospectionClientSecret"/>, this application's client at the issuer.
    /// </summary>
    public bool UseIntrospection { get; set; }

    public string? IntrospectionClientId { get; set; }

    public string? IntrospectionClientSecret { get; set; }

    /// <summary>How long an active introspection answer is reused, at most until the token expires.</summary>
    public TimeSpan IntrospectionCacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The scopes agents ask this server for (RFC 9728 <c>scopes_supported</c> and the challenge's <c>scope</c>):
    /// <c>mcp:read</c> and <c>mcp:write</c>. A server that names its scopes differently: list its names here.
    /// </summary>
    public IList<string> ScopesSupported { get; set; } = ["mcp:read", "mcp:write"];

    /// <summary>
    /// The scope a token must carry for the agent to make changes (D22): in this setup only, the token's scope is a real
    /// cap beside the user's grant, because the user cannot be stepped up through another server's consent. Read from
    /// <c>scope</c> or <c>scp</c>.
    /// </summary>
    public string WriteScope { get; set; } = "mcp:write";
}
