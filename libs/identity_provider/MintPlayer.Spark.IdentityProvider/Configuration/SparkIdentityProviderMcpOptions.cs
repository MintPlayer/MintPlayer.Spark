using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.IdentityProvider.Configuration;

/// <summary>
/// The authorization server for AI agents (<c>idp.ForMcp()</c>, <c>idp.ForMcpResource(…)</c>,
/// <c>Spark:IdentityProvider:Mcp</c>; MCP PRD D4, D5): agents sign the user in here and get a token for an MCP endpoint,
/// this application's own (setup 3) or another application's (its setup 2).
/// </summary>
/// <remarks>
/// Turns on RFC 8414 metadata, Client ID Metadata Documents, anonymous registration for loopback redirects, the
/// <c>mcp:read</c>/<c>mcp:write</c> scopes and one API resource per MCP endpoint whose audience is its canonical URL,
/// refresh rotation with family revocation, and the agent consent screen. Nothing else of the provider changes.
/// </remarks>
public sealed class SparkIdentityProviderMcpOptions
{
    /// <summary>
    /// Whether this application's own MCP endpoint (<c>spark.AddMcp(...)</c>) is one of the endpoints agents sign in for.
    /// <c>ForMcp()</c> sets it. Left unset, it is on when <see cref="Resources"/> is empty (a
    /// <c>Spark:IdentityProvider:Mcp</c> section on its own means this application's endpoint) and off when other
    /// applications' endpoints are listed, so a provider without an MCP endpoint of its own needs no setting.
    /// </summary>
    public bool? LocalEndpoint { get; set; }

    /// <summary>
    /// Other applications' MCP endpoints this server signs agents in for: <c>idp.ForMcpResource(url)</c>, or
    /// <c>Spark:IdentityProvider:Mcp:Resources</c> (<c>[ { "Url": "https://hr.example.com/mcp", "DisplayName": "HR" } ]</c>).
    /// </summary>
    public List<SparkIdentityProviderMcpResource> Resources { get; set; } = [];

    /// <summary>Whether this application's own endpoint is served: <see cref="LocalEndpoint"/>, else only when no other endpoint is listed.</summary>
    internal bool ServesLocalEndpoint => LocalEndpoint ?? Resources.Count == 0;

    /// <summary>
    /// Hosts whose Client ID Metadata Documents are accepted (exact host names, case-insensitive). Empty, the
    /// default: any HTTPS host on a public address (MCP PRD D5). A non-empty list refuses every other host.
    /// </summary>
    public List<string> MetadataDocumentHosts { get; set; } = [];

    /// <summary>The largest metadata document fetched. Default 5 KiB (the size the CIMD draft recommends).</summary>
    public int MetadataDocumentMaxBytes { get; set; } = 5 * 1024;

    /// <summary>How long one metadata-document fetch may take. Default 5 s.</summary>
    public TimeSpan MetadataDocumentTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a fetched document is used before it is fetched again at the next sign-in. Default 1 hour.</summary>
    public TimeSpan MetadataDocumentCacheFor { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Anonymous dynamic registration (RFC 7591 without an initial access token) for clients whose redirect URIs are
    /// all loopback (or VS Code's own). Default on: Gemini CLI registers only this way (MCP PRD D5, §4.9).
    /// </summary>
    public bool DynamicRegistration { get; set; } = true;

    /// <summary>
    /// Registrations and metadata-document fetches one address may cause per hour. Both make this server store or
    /// fetch something for an anonymous caller. Default 20; 0 or less is unlimited.
    /// </summary>
    public int RegistrationsPerHour { get; set; } = 20;

    /// <summary>A dynamically registered client unused this long is deleted, with its grants. Default 30 days.</summary>
    public TimeSpan UnusedClientLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>The access token lifetime of agent clients. Default 60 minutes.</summary>
    public int AccessTokenLifetimeMinutes { get; set; } = 60;

    /// <summary>The refresh token lifetime of agent clients; each refresh rotates it. Default 30 days.</summary>
    public int RefreshTokenLifetimeDays { get; set; } = 30;
}

/// <summary>
/// Another application's MCP endpoint that accepts this server's agent tokens (<c>idp.ForMcpResource(url)</c>; that
/// application's setup 2, MCP PRD D22). Its tokens carry <c>aud</c> = <see cref="Url"/> and nothing else.
/// </summary>
public sealed class SparkIdentityProviderMcpResource
{
    /// <summary>
    /// The endpoint's canonical URL, exactly as that application publishes it (its <c>Spark:Mcp:CanonicalUrl</c>): the
    /// <c>resource</c> agents ask for and the only audience their tokens get. https, or http on a loopback host for local
    /// development; no query, fragment or user info; a trailing <c>/</c> is dropped, as the Mcp package drops it.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The application's name on the consent screen ("Spark HR"). Default: the URL's host and port.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// The most a token for this endpoint may carry: <c>Read</c>, or <c>Read, Write</c> (the default). Without
    /// <c>Write</c> the consent screen offers reading only and <c>mcp:write</c> is never granted. That application still
    /// decides for itself: its own layers and the user's grant there (D22) only narrow this.
    /// </summary>
    public EAgentPermission Permissions { get; set; } = EAgentPermission.Read | EAgentPermission.Write;
}
