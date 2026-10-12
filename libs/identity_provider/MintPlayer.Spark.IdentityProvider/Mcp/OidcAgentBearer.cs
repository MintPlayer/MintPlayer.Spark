using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Agents;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Mcp;

/// <summary>
/// Setup 3 for the Mcp package (MCP PRD D4): the embedded authorization server's access tokens on <c>/mcp</c>. The
/// token's <c>sub</c> is the local user id, so the user's groups come from the store per request (§4.6).
/// </summary>
internal sealed class OidcAgentCredentialScheme(
    AgentGrantStore grants,
    SparkIdentityProviderOptions options,
    IHostEnvironment environment) : IAgentCredentialScheme
{
    public const string SchemeName = "Spark.IdentityProvider.Agent";

    /// <summary>The claim carrying the agent grant's id, set by <see cref="OidcAgentBearerHandler"/>.</summary>
    public const string GrantClaim = "spark_agent_grant";

    public EAgentAuthentication Setup => EAgentAuthentication.EmbeddedAuthorizationServer;

    public string Scheme => SchemeName;

    public bool SubjectIsLocalUserId => true;

    public IReadOnlyList<string> ScopesSupported { get; } = [OidcAgentClients.ReadScope, OidcAgentClients.WriteScope];

    /// <summary>The configured issuer; the request's origin only in Development without one, as <see cref="OidcIssuer"/> does.</summary>
    public string? AuthorizationServer(string requestOrigin)
        => !string.IsNullOrWhiteSpace(options.Issuer) ? options.Issuer.TrimEnd('/')
            : environment.IsDevelopment() ? requestOrigin
            : null;

    public async ValueTask<AgentCredential?> DescribeAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (principal.FindFirst(GrantClaim)?.Value is not { Length: > 0 } grantId)
            return null;

        var grant = await grants.GetAsync(grantId, cancellationToken);
        if (grant is null)
            return null;

        var agent = new AgentInformation(grant.Kind, grant.ClientId, grant.VerifiedHost, grant.Name, principal) { GrantId = grant.Id };
        return new AgentCredential(agent, grant.ToScope());
    }
}

/// <summary>
/// Validates an access token of the embedded server for <c>/mcp</c>: signature and issuer (the key ring), type
/// <c>at+jwt</c>, lifetime, a live token record and OAuth grant (revocation is immediate), audience exactly the MCP
/// canonical URL (a token for another resource is refused, D5 G4), no proof-of-possession binding (which <c>/mcp</c>
/// cannot check), a user who exists and is not locked out, and an agent grant linked to that OAuth grant with the
/// user's current security stamp (D11, D12). The principal carries the role and group claims of the user's cookie
/// (<see cref="IAgentRoleClaimsProvider"/>), made per request.
/// </summary>
internal sealed partial class OidcAgentBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IDocumentStore store,
    OidcKeyRing keys,
    OidcIssuer issuer,
    OidcMcpResources resources,
    AgentGrantStore grants,
    AgentRequestUser users,
    IAgentRoleClaimsProvider roleClaims,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult();
        var token = header[BearerPrefix.Length..].Trim();
        // Not a JWT (a personal access token, an upload token): another scheme's, or nobody's.
        if (token.Count(c => c == '.') != 2)
            return AuthenticateResult.NoResult();

        using var session = store.OpenAsyncSession();
        var resolved = await AccessTokens.ResolveAsync(session, keys, token, issuer.Resolve(Request), Context.RequestAborted);
        if (resolved is null)
            return Refuse("not a token of this server");
        if (!string.Equals(resolved.Jwt.Typ, "at+jwt", StringComparison.OrdinalIgnoreCase))
            return Refuse("not an access token");
        if (!resolved.IsActive)
            return Refuse("expired, revoked or withdrawn");
        if (resources.Local?.Audience is not { } audience || resolved.Audiences.Count != 1 || !string.Equals(resolved.Audiences[0], audience, StringComparison.Ordinal))
            return Refuse("audience is not this MCP endpoint");
        if (resolved.ConfirmationJkt is not null || resolved.ConfirmationX5t is not null)
            return Refuse("sender-constrained token");
        if (resolved.Subject is not { Length: > 0 } userId || resolved.ClientId is not { Length: > 0 } clientId)
            return Refuse("no subject or client");

        var user = await users.GetAsync(userId, Context.RequestAborted);
        if (user is null)
            return Refuse("user deleted");
        if (AgentRequestUser.IsLockedOut(user, time.GetUtcNow()))
            return Refuse("user locked out");

        var grant = await grants.GetAsync(AgentGrantIds.Application(userId, clientId), Context.RequestAborted);
        if (grant is null || !string.Equals(grant.UserId, userId, StringComparison.Ordinal)
            || !string.Equals(grant.OidcGrantId, resolved.Record?.AuthorizationId, StringComparison.Ordinal))
            return Refuse("no agent grant");
        if (!string.Equals(user.SecurityStamp ?? string.Empty, grant.SecurityStamp ?? string.Empty, StringComparison.Ordinal))
            return Refuse("security stamp changed");

        await grants.TouchAsync(grant, Context.RequestAborted);

        // The roles and groups the user's sign-in cookie carries, made now: application code that tests User.IsInRole
        // treats the agent as the user, and a removed role is gone on the next call.
        var roles = await roleClaims.GetAsync(user, Context.RequestAborted);
        var identity = new ClaimsIdentity(
            [
                new Claim("sub", userId),
                // The local id under the name application code reads too (Actions, row policies, quotas), as a cookie carries it.
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim("name", user.UserName ?? userId),
                new Claim("client_id", clientId),
                new Claim("scope", resolved.Scope ?? string.Empty),
                new Claim(OidcAgentCredentialScheme.GrantClaim, grant.Id!),
                .. roles.Claims,
            ],
            Scheme.Name, "name", roles.RoleClaimType);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private AuthenticateResult Refuse(string reason)
    {
        LogRefused(Logger, reason);
        return AuthenticateResult.Fail("The access token is not valid for this MCP endpoint.");
    }

    [LoggerMessage(EventId = 7410, EventName = "SparkAgentAccessTokenRefused", Level = LogLevel.Information,
        Message = "Agent access token refused on /mcp ({Reason}).")]
    private static partial void LogRefused(ILogger logger, string reason);
}
