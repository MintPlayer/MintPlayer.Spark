using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Authorization.ResourceServer;

namespace MintPlayer.Spark.Authorization.Agents;

/// <summary>The scheme names and claims of setup 2 (MCP PRD D4, D22).</summary>
internal static class ExternalAgentServer
{
    /// <summary>The scheme the Mcp package runs on <c>/mcp</c>: validation, then the mapping to the local user.</summary>
    public const string Scheme = "Spark.Agent.External";

    /// <summary>The inner scheme that validates the external token only (JWT bearer or introspection). Never run alone.</summary>
    public const string TokenScheme = "Spark.Agent.External.Token";

    /// <summary>The claim carrying the agent grant's id.</summary>
    public const string GrantClaim = "spark_agent_grant";

    /// <summary>The token's scopes, from <c>scope</c> (RFC 9068, introspection) or <c>scp</c> (Entra), space-separated or repeated.</summary>
    public static IEnumerable<string> ScopesOf(ClaimsPrincipal principal)
        => principal.FindAll(c => c.Type is "scope" or "scp")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal);

    /// <summary>The OAuth client the token was issued to: <c>azp</c> (OpenID, Entra v2) or <c>client_id</c> (RFC 9068, introspection).</summary>
    public static string? ClientIdOf(ClaimsPrincipal principal)
        => principal.FindFirst("azp")?.Value is { Length: > 0 } azp ? azp
            : principal.FindFirst("client_id")?.Value is { Length: > 0 } clientId ? clientId
            : null;
}

/// <summary>
/// Setup 2 for the Mcp package (MCP PRD D4, D22): an external authorization server's access tokens. The scheme's
/// principal carries the <b>local</b> user id as <c>sub</c> (the handler maps it through the linked login), so it
/// declares <see cref="SubjectIsLocalUserId"/> truthfully and the user's groups come from the store per request (§4.6).
/// </summary>
internal sealed class ExternalAgentServerScheme(SparkExternalAgentServerOptions options, AgentGrantStore grants) : IAgentCredentialScheme
{
    public EAgentAuthentication Setup => EAgentAuthentication.ExternalAuthorizationServer;

    public string Scheme => ExternalAgentServer.Scheme;

    public bool SubjectIsLocalUserId => true;

    public IReadOnlyList<string> ScopesSupported => options.ScopesSupported.ToList();

    /// <summary>Always the configured issuer: the agent signs in there, whatever the request's origin.</summary>
    public string? AuthorizationServer(string requestOrigin) => options.Issuer;

    public async ValueTask<AgentCredential?> DescribeAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (principal.FindFirst(ExternalAgentServer.GrantClaim)?.Value is not { Length: > 0 } grantId)
            return null;

        var grant = await grants.GetAsync(grantId, cancellationToken);
        if (grant is null || grant.RevokedAt is not null)
            return null;

        var agent = new AgentInformation(EAgentKind.ExternalAuthorizationServer, grant.ClientId, VerifiedHost: null, grant.Name, principal)
        {
            GrantId = grant.Id,
        };
        return new AgentCredential(agent, ScopeOf(grant, ExternalAgentServer.ScopesOf(principal), options.WriteScope));
    }

    /// <summary>
    /// The grant as the permission layer applies it, capped by the token (D22, R3-Q18): <c>Write</c> needs the user's
    /// choice on the grant <b>and</b> <paramref name="writeScope"/> among the token's scopes. Only here is a token's scope
    /// a cap: the user cannot be sent through another server's consent to step it up.
    /// </summary>
    internal static AgentGrantScope ScopeOf(AgentGrant grant, IEnumerable<string> tokenScopes, string writeScope)
    {
        var scope = grant.ToScope();
        return tokenScopes.Contains(writeScope, StringComparer.Ordinal)
            ? scope
            : scope with { Permissions = scope.Permissions & ~EAgentPermission.Write };
    }
}

/// <summary>
/// Accepts an external authorization server's access token on <c>/mcp</c> (MCP PRD D22): validated by the inner
/// <see cref="ExternalAgentServer.TokenScheme"/> (signature, pinned issuer, audience = the MCP canonical URL, lifetime,
/// <c>at+jwt</c>, or introspection), then mapped to a local user <b>only</b> through a login of the bound sign-in provider
/// whose key is the token's subject, never by email. Creates the agent's shadow grant on its first call.
/// </summary>
/// <remarks>
/// Refused: a token the inner scheme refuses, a sender-constrained token (<c>/mcp</c> cannot check the proof), a token
/// without a subject or a client, a subject no user linked under that provider, a deleted or locked-out user, and a grant
/// the user disconnected. A refusal never says which check failed, and nothing here logs the token. The principal carries
/// the local user id as <c>sub</c>, the grant id, the client id and the token's scopes: groups and rights come from the
/// local user, never from the token's role or group claims.
/// </remarks>
internal sealed partial class ExternalAgentTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    SparkExternalAgentServerOptions options,
    IAgentLinkedLogins logins,
    AgentGrantStore grants,
    AgentRequestUser users,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult();
        // A personal access token is setup 1's.
        if (header.AsSpan(BearerPrefix.Length).TrimStart().StartsWith(PersonalAccessTokens.Prefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult();

        var validated = await Context.AuthenticateAsync(ExternalAgentServer.TokenScheme);
        if (!validated.Succeeded)
            return Refuse("not a valid token of the external server");

        var token = validated.Principal!;
        if (token.HasClaim(c => c.Type == "cnf"))
            return Refuse("sender-constrained token");
        if (token.FindFirst(options.SubjectClaim)?.Value is not { Length: > 0 } subject)
            return Refuse("no subject");
        if (ExternalAgentServer.ClientIdOf(token) is not { } clientId)
            return Refuse("no client");

        // D22: the linked login of the bound provider, by subject. The issuer is pinned by the inner scheme, so a subject
        // of another server never reaches this lookup; an email claim is never consulted.
        var userId = await logins.FindUserIdAsync(options.LoginProvider, subject, Context.RequestAborted);
        if (userId is null)
            return Refuse("no linked login");

        var user = await users.GetAsync(userId, Context.RequestAborted);
        if (user is null)
            return Refuse("user deleted");
        if (AgentRequestUser.IsLockedOut(user, time.GetUtcNow()))
            return Refuse("user locked out");

        // The shadow grant (D22, R3-Q18): created on first use, everything exposed, read only.
        var id = AgentGrantIds.ExternalAuthorizationServer(userId, options.Issuer, clientId);
        var grant = await grants.GetAsync(id, Context.RequestAborted)
            ?? await grants.CreateIfMissingAsync(new AgentGrant
            {
                Id = id,
                UserId = userId,
                Kind = EAgentKind.ExternalAuthorizationServer,
                ClientId = clientId,
                Issuer = options.Issuer,
                Permissions = EAgentPermission.Read,
                Types = null,
                CreatedAt = time.GetUtcNow(),
            }, Context.RequestAborted);
        if (grant is null || grant.Kind != EAgentKind.ExternalAuthorizationServer || !string.Equals(grant.UserId, userId, StringComparison.Ordinal))
            return Refuse("no agent grant");
        if (grant.RevokedAt is not null)
            return Refuse("disconnected by the user");

        await grants.TouchAsync(grant, Context.RequestAborted);

        var identity = new ClaimsIdentity(
            [
                new Claim("sub", userId),
                new Claim("name", user.UserName ?? userId),
                new Claim("client_id", clientId),
                new Claim("scope", string.Join(' ', ExternalAgentServer.ScopesOf(token))),
                new Claim(ExternalAgentServer.GrantClaim, grant.Id!),
            ],
            Scheme.Name, "name", "role");
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private AuthenticateResult Refuse(string reason)
    {
        LogRefused(Logger, reason);
        return AuthenticateResult.Fail("The access token is not valid for this MCP endpoint.");
    }

    [LoggerMessage(EventId = 7330, EventName = "SparkExternalAgentTokenRefused", Level = LogLevel.Information,
        Message = "External agent access token refused on /mcp ({Reason}).")]
    private static partial void LogRefused(ILogger logger, string reason);
}

/// <summary>Finds the local user who linked an external login (MCP PRD D22). Generic over the user type behind it.</summary>
internal interface IAgentLinkedLogins
{
    /// <summary>The id of the user with a login of <paramref name="loginProvider"/> keyed <paramref name="providerKey"/>, else <see langword="null"/>.</summary>
    Task<string?> FindUserIdAsync(string loginProvider, string providerKey, CancellationToken cancellationToken = default);
}

/// <summary>Identity's own login lookup (<c>FindByLoginAsync</c>, provider and key both matched exactly), as the web's external sign-in uses it.</summary>
internal sealed class AgentLinkedLogins<TUser>(UserManager<TUser> users) : IAgentLinkedLogins
    where TUser : SparkUser
{
    public async Task<string?> FindUserIdAsync(string loginProvider, string providerKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await users.FindByLoginAsync(loginProvider, providerKey))?.Id;
    }
}
