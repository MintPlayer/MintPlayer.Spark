using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Agents;
using MintPlayer.Spark.Authorization.Extensions;

namespace MintPlayer.Spark.Authorization.ResourceServer;

public static class SparkExternalAgentServerExtensions
{
    /// <summary>
    /// Lets AI agents sign in at an external authorization server and call <c>/mcp</c> with its access tokens: setup 2
    /// of the MCP PRD (D4, D22). The Mcp package then lists <paramref name="issuer"/> in its RFC 9728 document and
    /// challenge, and <c>EAgentAuthentication.ExternalAuthorizationServer</c> is in effect.
    /// </summary>
    /// <param name="issuer">The server's issuer, pinned (<see cref="SparkExternalAgentServerOptions.Issuer"/>).</param>
    /// <param name="loginProvider">
    /// The sign-in provider for the same server (<see cref="SparkExternalAgentServerOptions.LoginProvider"/>): a token
    /// maps to the local user who linked that provider's account, by subject, never by email.
    /// </param>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Tokens are validated as <see cref="SparkResourceServerExtensions.AddSparkResourceServer"/> validates them
    /// (signature from the issuer's keys, issuer, lifetime, <c>at+jwt</c>; introspection for opaque tokens), under a scheme
    /// of their own that is <b>not</b> a Spark web credential: the token opens <c>/mcp</c> and nothing else.</item>
    /// <item>The audience is the MCP canonical URL (<c>IAgentExposure.ResourceUrl</c>), not configurable: startup fails
    /// without <c>spark.AddMcp(...)</c> or a canonical URL.</item>
    /// <item>An unmapped token is refused (401), never run as anonymous. Groups and rights come from the local user.</item>
    /// <item>The agent's grant (<c>AgentGrants/{userId}/ext/…</c>) is created on its first call with read access to
    /// everything exposed; changes need the user's toggle under Connected applications and
    /// <see cref="SparkExternalAgentServerOptions.WriteScope"/> in the token.</item>
    /// </list>
    /// Call after <c>spark.AddAuthentication&lt;TUser&gt;()</c> and the sign-in provider.
    /// </remarks>
    public static ISparkBuilder AddExternalAgentAuthorizationServer(
        this ISparkBuilder builder, string issuer, string loginProvider, Action<SparkExternalAgentServerOptions>? configure = null)
    {
        if (builder.Registry.IdentityUserType is null)
            throw new InvalidOperationException(
                "AddExternalAgentAuthorizationServer maps tokens to users of spark.AddAuthentication<TUser>(); call that first.");

        var options = new SparkExternalAgentServerOptions { Issuer = issuer, LoginProvider = loginProvider };
        configure?.Invoke(options);

        if (string.IsNullOrWhiteSpace(options.Issuer))
            throw new InvalidOperationException("AddExternalAgentAuthorizationServer requires the server's issuer.");
        if (string.IsNullOrWhiteSpace(options.LoginProvider))
            throw new InvalidOperationException(
                "AddExternalAgentAuthorizationServer requires the sign-in provider of the same server: a token maps to a user "
                + "only through a login of that provider the user linked (never by email).");
        if (string.IsNullOrWhiteSpace(options.SubjectClaim) || string.IsNullOrWhiteSpace(options.WriteScope))
            throw new InvalidOperationException("AddExternalAgentAuthorizationServer requires a subject claim and a write scope.");

        // The audience is filled in at startup from the MCP canonical URL (below); AddTokenScheme reads it on first use.
        var tokens = new SparkResourceServerOptions
        {
            Authority = options.Issuer,
            RequireHttpsMetadata = options.RequireHttpsMetadata,
            RequireAccessTokenType = options.RequireAccessTokenType,
            UseIntrospection = options.UseIntrospection,
            IntrospectionClientId = options.IntrospectionClientId,
            IntrospectionClientSecret = options.IntrospectionClientSecret,
            IntrospectionCacheDuration = options.IntrospectionCacheDuration,
        };
        SparkResourceServerExtensions.ValidateIssuer(tokens, nameof(AddExternalAgentAuthorizationServer));
        SparkResourceServerExtensions.AddTokenScheme(builder.Services, ExternalAgentServer.TokenScheme, tokens);

        builder.Services.AddSingleton(options);
        builder.Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ExternalAgentTokenHandler>(ExternalAgentServer.Scheme, _ => { });
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentCredentialScheme, ExternalAgentServerScheme>());

        builder.Registry.AddMiddleware(app =>
        {
            using var scope = app.ApplicationServices.CreateScope();
            var exposure = scope.ServiceProvider.GetService<IAgentExposure>()
                ?? throw new InvalidOperationException(
                    "AddExternalAgentAuthorizationServer needs the MCP server: add spark.AddMcp(...) to the application.");
            tokens.Audience = exposure.ResourceUrl
                ?? throw new InvalidOperationException(
                    "AddExternalAgentAuthorizationServer needs the MCP endpoint's canonical URL, the only audience it accepts: "
                    + "set Spark:Mcp:CanonicalUrl or Spark:Auth:PublicBaseUrl.");

            var schemes = app.ApplicationServices.GetRequiredService<IAuthenticationSchemeProvider>();
            var provider = schemes.GetSchemeAsync(options.LoginProvider).GetAwaiter().GetResult();
            if (provider is null || !SparkExternalSchemeGuard.IsRemote(provider.HandlerType))
                throw new InvalidOperationException(
                    $"AddExternalAgentAuthorizationServer binds '{options.Issuer}' to the sign-in provider '{options.LoginProvider}', "
                    + "which is not registered. Configure the same server as a sign-in provider (spark.AddOpenIdConnect(...)) so "
                    + "users can link it: an agent's token maps only to a user who did.");
        });

        return builder;
    }
}
