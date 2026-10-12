using System.Net;
using System.Security.Cryptography;
using System.Text;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.IdentityProvider.Mcp;

/// <summary>
/// The rules for AI agent clients of the embedded authorization server (<c>idp.ForMcp()</c>, MCP PRD D5, D7):
/// their natural ids, what kind of agent each is, and which redirect URIs they may use.
/// </summary>
internal static class OidcAgentClients
{
    /// <summary>The MCP API resource's name, the prefix of its scopes (<c>mcp:read</c>), and the key of <c>OidcResources/mcp</c>.</summary>
    public const string ResourceName = "mcp";

    /// <summary>Read access through the agent's grant. Never a cap: the grant decides (D6).</summary>
    public const string ReadScope = "mcp:read";

    /// <summary>Changes through the agent's grant; exists only so a missing <c>Write</c> can answer <c>insufficient_scope</c> (D6).</summary>
    public const string WriteScope = "mcp:write";

    public const string OfflineAccess = "offline_access";

    /// <summary>What an agent client may ask for, and what RFC 8414/RFC 9728 advertise (D5).</summary>
    public static readonly IReadOnlyList<string> Scopes = [ReadScope, WriteScope, OfflineAccess];

    /// <summary>
    /// The property an authorization request, its code and its refresh tokens carry when the request is for the MCP
    /// resource. Copied from one to the next, so every token of the chain knows it without re-deriving it.
    /// </summary>
    public const string RequestProperty = "mcp";

    /// <summary>The prefix of a dynamically registered agent's client id.</summary>
    public const string DynamicClientIdPrefix = "mcp_";

    /// <summary>
    /// The non-loopback redirect URIs an agent may register anonymously or use from its metadata document beside the
    /// loopback ones: exactly VS Code's (plan M3b; read from microsoft/vscode at 42bad02a,
    /// <c>src/vs/base/common/oauth.ts:947-972</c> and <c>extHostAuthentication.ts:750</c>; see the PRD decision log).
    /// Never "any https": PKCE protects the code, but an arbitrary https page would receive it.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownRedirectUris =
    [
        "https://vscode.dev/redirect",
        "https://insiders.vscode.dev/redirect",
    ];

    /// <summary>
    /// The document id of the agent client <paramref name="clientId"/>: a point load, so a client registered a moment
    /// ago is found at once (an index query would be eventually consistent), and the id never contains the URL.
    /// </summary>
    public static string ApplicationId(string clientId)
        => "OidcApplications/agent-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(clientId)))[..32];

    public static bool IsAgent(OidcApplication app) => app.AgentRegistration is not null;

    /// <summary>How the agent obtained its token (D7). An application registered by a person is <see cref="EAgentKind.PreRegistered"/>.</summary>
    public static EAgentKind KindOf(OidcApplication app) => app.AgentRegistration switch
    {
        OidcAgentRegistrations.MetadataDocument => EAgentKind.MetadataDocument,
        OidcAgentRegistrations.DynamicRegistration => EAgentKind.DynamicRegistration,
        _ => EAgentKind.PreRegistered,
    };

    /// <summary>The host a metadata document was fetched from over HTTPS; the only verified fact about an agent (D7).</summary>
    public static string? VerifiedHostOf(OidcApplication app)
        => app.AgentRegistration == OidcAgentRegistrations.MetadataDocument && Uri.TryCreate(app.ClientId, UriKind.Absolute, out var url)
            ? url.Host
            : null;

    /// <summary>
    /// Whether <paramref name="redirectUri"/> may receive this client's code: a registered URI exactly, or, for an
    /// agent client with a loopback URI registered, any loopback URI (RFC 8252 §7.3 and §8.3: <c>localhost</c>,
    /// <c>127.0.0.1</c> and <c>[::1]</c>, any port, any path; MCP PRD D5, spike S3). A native agent picks a free port
    /// per sign-in, and its metadata document lists a portless URI.
    /// </summary>
    public static bool RedirectAllowed(OidcApplication app, string redirectUri)
    {
        if (app.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
            return true;
        return IsAgent(app) && IsLoopback(redirectUri) && app.RedirectUris.Any(IsLoopback);
    }

    /// <summary>
    /// A loopback redirect URI: <c>http</c>, host <c>localhost</c>, <c>127.0.0.1</c> or <c>[::1]</c>, no user info, no
    /// fragment. The scheme check is load-bearing (on Linux <c>Uri.TryCreate("/x")</c> succeeds as <c>file:///x</c>).
    /// </summary>
    public static bool IsLoopback(string? uri)
    {
        if (string.IsNullOrEmpty(uri) || uri.Contains('#') || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return false;
        if (parsed.Scheme != Uri.UriSchemeHttp || parsed.UserInfo.Length > 0)
            return false;
        return string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase)
               || (IPAddress.TryParse(parsed.Host.Trim('[', ']'), out var address)
                   && (address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback)));
    }

    /// <summary>A redirect URI an agent may register without anyone vouching for it: loopback, or VS Code's.</summary>
    public static bool AcceptableWithoutRegistrant(string? uri)
        => IsLoopback(uri) || (uri is not null && KnownRedirectUris.Contains(uri, StringComparer.Ordinal));

    /// <summary>The agent client's settings, shared by metadata-document and dynamic registration.</summary>
    public static void ApplyAgentDefaults(OidcApplication app, Configuration.SparkIdentityProviderMcpOptions options)
    {
        app.ClientType = "public";
        app.TokenEndpointAuthMethod = OidcClientAuthMethods.None;
        app.RequirePkce = true;
        app.AllowedGrantTypes = ["authorization_code", "refresh_token"];
        app.Scopes = [.. Scopes.Select(s => new OidcApplicationScope { Name = s, Status = OidcScopeStatuses.Approved })];
        // Every sign-in shows the agent consent screen, where the user chooses types and read/changes: a remembered
        // consent would skip the choice, and an implicit one would make it for them.
        app.ConsentType = "explicit";
        app.AllowRememberConsent = false;
        app.Mode = OidcApplicationModes.Live;
        // D5 gap G10: the subject is the local user id, which the grant's natural id and the store-backed groups need.
        app.SubjectType = "public";
        app.AccessTokenLifetimeMinutes = options.AccessTokenLifetimeMinutes;
        app.RefreshTokenLifetimeDays = options.RefreshTokenLifetimeDays;
        app.IdTokenLifetimeMinutes = OidcApplication.DefaultIdTokenLifetimeMinutes;
    }

    /// <summary>A display name an agent chose: trimmed, at most 100 characters, no control characters; else <paramref name="fallback"/>.</summary>
    public static string NameOf(string? name, string fallback)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsControl))
            return fallback;
        return trimmed.Length > 100 ? trimmed[..100] : trimmed;
    }
}
