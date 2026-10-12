using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.IdentityProvider.Configuration;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// What this provider maps and advertises, decided once at startup from <see cref="SparkIdentityProviderOptions.Flows"/>,
/// <see cref="SparkIdentityProviderOptions.Endpoints"/> and the agent server (<c>ForMcp()</c>, <c>ForMcpResource()</c>).
/// </summary>
/// <remarks>
/// Every endpoint's <c>IsEnabled</c>, the token endpoint's grant switch and both metadata documents read this one object, so
/// a route that is not mapped is never advertised and a grant that is not advertised is never honoured. A combination that
/// cannot work refuses startup in <see cref="For"/>, naming the dependency, instead of mapping something half-usable.
/// </remarks>
internal sealed class OidcSurface
{
    private OidcSurface(SparkIdentityProviderOptions options)
    {
        var flows = options.Flows;
        AuthorizationCode = flows.HasFlag(SparkIdentityProviderFlows.AuthorizationCode);
        RefreshToken = flows.HasFlag(SparkIdentityProviderFlows.RefreshToken);
        ClientCredentials = flows.HasFlag(SparkIdentityProviderFlows.ClientCredentials);
        DeviceCode = flows.HasFlag(SparkIdentityProviderFlows.DeviceCode);
        TokenExchange = flows.HasFlag(SparkIdentityProviderFlows.TokenExchange);

        var endpoints = options.Endpoints;
        UserInfo = endpoints.HasFlag(SparkIdentityProviderEndpoints.UserInfo);
        EndSession = endpoints.HasFlag(SparkIdentityProviderEndpoints.EndSession);
        Introspection = endpoints.HasFlag(SparkIdentityProviderEndpoints.Introspection);
        Revocation = endpoints.HasFlag(SparkIdentityProviderEndpoints.Revocation);
        PushedAuthorization = endpoints.HasFlag(SparkIdentityProviderEndpoints.PushedAuthorization);
        DeveloperRegistration = endpoints.HasFlag(SparkIdentityProviderEndpoints.DeveloperRegistration);
        DeveloperPortal = endpoints.HasFlag(SparkIdentityProviderEndpoints.DeveloperPortal);
        ConnectedApplications = endpoints.HasFlag(SparkIdentityProviderEndpoints.ConnectedApplications);
        Administration = endpoints.HasFlag(SparkIdentityProviderEndpoints.Administration);

        AgentRegistration = options.Mcp is { DynamicRegistration: true };

        // The discovery document's order, which it has always listed them in.
        GrantTypes =
        [
            .. AuthorizationCode ? ["authorization_code"] : Array.Empty<string>(),
            .. RefreshToken ? ["refresh_token"] : Array.Empty<string>(),
            .. ClientCredentials ? ["client_credentials"] : Array.Empty<string>(),
            .. DeviceCode ? [OidcDeviceCodes.GrantType] : Array.Empty<string>(),
            .. TokenExchange ? ["urn:ietf:params:oauth:grant-type:token-exchange"] : Array.Empty<string>(),
        ];
    }

    /// <summary>The surface <paramref name="options"/> describe, or a refusal to start naming every dependency they break.</summary>
    /// <exception cref="InvalidOperationException">A flow or endpoint needs one that is left out.</exception>
    public static OidcSurface For(SparkIdentityProviderOptions options)
    {
        var surface = new OidcSurface(options);
        var problems = surface.Problems(options).ToList();
        if (problems.Count == 1)
            throw new InvalidOperationException(problems[0]);
        if (problems.Count > 1)
            throw new InvalidOperationException(
                "The identity provider's flows and endpoints do not fit together:" + string.Concat(problems.Select(p => Environment.NewLine + "- " + p)));
        return surface;
    }

    /// <summary>The surface of the provider in <paramref name="services"/> (a singleton, registered by <c>AddIdentityProvider</c>).</summary>
    public static OidcSurface Of(IServiceProvider services) => services.GetRequiredService<OidcSurface>();

    /// <summary>The authorization code flow: <c>/connect/authorize</c>, the consent page and the grant.</summary>
    public bool AuthorizationCode { get; }

    /// <summary>The <c>refresh_token</c> grant; without it no refresh token is issued.</summary>
    public bool RefreshToken { get; }

    /// <summary>The <c>client_credentials</c> grant.</summary>
    public bool ClientCredentials { get; }

    /// <summary>The device grant, <c>/connect/device_authorization</c> and the user-code page.</summary>
    public bool DeviceCode { get; }

    /// <summary>Token exchange (RFC 8693).</summary>
    public bool TokenExchange { get; }

    /// <summary>A flow in which a user signs in at the provider's pages: the sign-in, two-factor and error pages exist.</summary>
    public bool Interactive => AuthorizationCode || DeviceCode;

    /// <summary><c>/connect/userinfo</c>.</summary>
    public bool UserInfo { get; }

    /// <summary><c>/connect/logout</c>, and the consent pages' "Not you?" link to it.</summary>
    public bool EndSession { get; }

    /// <summary><c>/connect/introspect</c>.</summary>
    public bool Introspection { get; }

    /// <summary><c>/connect/revoke</c>.</summary>
    public bool Revocation { get; }

    /// <summary><c>/connect/par</c>.</summary>
    public bool PushedAuthorization { get; }

    /// <summary>A developer's registration with an initial access token, and RFC 7592 management of it.</summary>
    public bool DeveloperRegistration { get; }

    /// <summary>The developer portal's API.</summary>
    public bool DeveloperPortal { get; }

    /// <summary><c>/connect/applications</c> and <c>/spark/identity-provider/applications</c>, and the consent pages' link to the former.</summary>
    public bool ConnectedApplications { get; }

    /// <summary><c>/spark/identity-provider/admin/keys</c>.</summary>
    public bool Administration { get; }

    /// <summary>Anonymous registration of AI agents (the agent server's <c>DynamicRegistration</c>).</summary>
    public bool AgentRegistration { get; }

    /// <summary><c>POST /connect/register</c>: someone may register, a developer or an agent.</summary>
    public bool Registration => DeveloperRegistration || AgentRegistration;

    /// <summary>The <c>grant_type</c> values <c>/connect/token</c> accepts, as the discovery document lists them.</summary>
    public IReadOnlyList<string> GrantTypes { get; }

    private IEnumerable<string> Problems(SparkIdentityProviderOptions options)
    {
        if (options.Flows == SparkIdentityProviderFlows.None)
            yield return "The identity provider has no flow (Flows is None), so it would issue no tokens. Choose at least one, or leave Flows at its default, All.";

        if (RefreshToken && !Interactive)
            yield return "Flows.RefreshToken needs Flows.AuthorizationCode or Flows.DeviceCode: refresh tokens are issued only in those flows.";
        if (PushedAuthorization && !AuthorizationCode)
            yield return "Endpoints.PushedAuthorization needs Flows.AuthorizationCode: a pushed request is used at /connect/authorize.";
        if (UserInfo && !Interactive)
            yield return "Endpoints.UserInfo needs Flows.AuthorizationCode or Flows.DeviceCode: userinfo describes a user who signed in in one of them.";
        if (EndSession && !Interactive)
            yield return "Endpoints.EndSession needs Flows.AuthorizationCode or Flows.DeviceCode: it ends the session a sign-in in one of them starts.";
        if (ConnectedApplications && !Interactive)
            yield return "Endpoints.ConnectedApplications needs Flows.AuthorizationCode or Flows.DeviceCode: users connect applications by signing in and consenting in one of them.";
        if (DeveloperRegistration && !DeveloperPortal)
            yield return "Endpoints.DeveloperRegistration needs Endpoints.DeveloperPortal: a developer registers with an initial access token the portal issues (POST /spark/identity-provider/developer/registration-token).";

        if (options.Mcp is { } mcp)
        {
            if (!AuthorizationCode)
                yield return "ForMcp()/ForMcpResource() (Spark:IdentityProvider:Mcp) needs Flows.AuthorizationCode: AI agents sign users in with the authorization code flow and PKCE.";

            // MCP PRD D22: the application behind another endpoint maps the agent's token to a user through the login its users
            // linked by signing in here, and Spark's OpenID Connect sign-in reads userinfo (GetClaimsFromUserInfoEndpoint).
            if (mcp.Resources.Count > 0 && !UserInfo)
                yield return $"ForMcpResource(\"{mcp.Resources[0].Url}\") needs Endpoints.UserInfo: that application's users sign in here to link their login (MCP PRD D22), "
                    + "and Spark's OpenID Connect sign-in reads userinfo. AgentsOnly() leaves it out: it is for a provider that signs agents in for its own /mcp only (ForMcp()).";
        }
    }
}
