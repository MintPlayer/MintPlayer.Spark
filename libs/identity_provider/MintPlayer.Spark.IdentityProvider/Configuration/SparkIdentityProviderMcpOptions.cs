namespace MintPlayer.Spark.IdentityProvider.Configuration;

/// <summary>
/// The embedded authorization server for AI agents (<c>idp.ForMcp()</c>, <c>Spark:IdentityProvider:Mcp</c>; MCP PRD
/// D4 setup 3, D5): agents sign the user in here and get a token for the application's <c>/mcp</c> endpoint.
/// </summary>
/// <remarks>
/// Turns on RFC 8414 metadata, Client ID Metadata Documents, anonymous registration for loopback redirects, the
/// <c>mcp:read</c>/<c>mcp:write</c> scopes and the API resource whose audience is the MCP canonical URL, refresh
/// rotation with family revocation, and the agent consent screen. Nothing else of the provider changes.
/// </remarks>
public sealed class SparkIdentityProviderMcpOptions
{
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
