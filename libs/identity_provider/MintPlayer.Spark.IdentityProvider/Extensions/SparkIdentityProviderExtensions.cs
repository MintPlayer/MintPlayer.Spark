using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Endpoints;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Expiration;

namespace MintPlayer.Spark.IdentityProvider.Extensions;

public static class SparkIdentityProviderExtensions
{
    /// <summary>The application's local-credential mode.</summary>
    /// <remarks>
    /// Required, not optional: the provider refuses to start without <c>AddAuthentication&lt;TUser&gt;()</c>
    /// (<see cref="OidcUserEndpoints.RequireUserType"/>, checked before anything is mapped), which is
    /// also what registers these options. The old fallback to <see cref="SparkLocalCredentials.Full"/>
    /// for "used without <c>AddAuthentication</c>" described a configuration that can no longer start.
    /// </remarks>
    internal static SparkLocalCredentials LocalCredentialsOf(IServiceProvider services) =>
        services.GetRequiredService<SparkAuthenticationOptions>().LocalCredentials;

    /// <summary>
    /// Configures this Spark application as an OIDC Identity Provider.
    /// Registers OIDC endpoints, signing key service, token generator,
    /// and token cleanup background service.
    /// </summary>
    public static ISparkBuilder AddIdentityProvider(
        this ISparkBuilder builder,
        Action<SparkIdentityProviderOptions>? configure = null)
    {
        var options = new SparkIdentityProviderOptions();
        // Spark:IdentityProvider first, the lambda second: code wins over configuration, as everywhere in Spark.
        builder.Configuration?.GetSection("Spark:IdentityProvider").Bind(options);
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        // Register services
        builder.Services.AddSingleton(sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            return new OidcSigningKeyService(env, options.SigningKeyPath);
        });
        builder.Services.AddSingleton<OidcTokenGenerator>();
        builder.Services.AddSingleton<OidcIssuer>();
        // The typed /connect pages reach their HttpContext through the accessor (D8). AddSpark
        // registers it too; registering it here keeps the provider from depending on that.
        builder.Services.AddHttpContextAccessor();

        // Validation of the OIDC admin screens (#482: interceptors, not Actions-class overrides).
        builder.AddInterceptor<Interceptors.OidcApplicationInterceptors>();
        builder.AddInterceptor<Interceptors.OidcResourceInterceptors>();

        // The developer portal (PRD D2): developer status on the user document, membership of the
        // bound developers group derived from it per request, and the audit trail (D9).
        builder.Services.AddSingleton<OidcDevelopers>();
        var moduleRegistry = builder.Registry;
        builder.Services.AddSingleton(sp => new OidcUserDocuments(moduleRegistry, sp.GetRequiredService<IDocumentStore>()));
        builder.Services.AddSingleton<OidcAudit>();
        builder.Services.AddSingleton<OidcPortalMail>();
        builder.Services.AddSingleton<OidcPortalLinks>();
        builder.Services.AddSingleton<OidcInvitations>();
        builder.Services.AddSingleton<OidcTeamMail>();
        builder.Services.AddScoped<OidcPortalAccess>();
        builder.Services.AddScoped<ConnectText>();
        builder.Services.AddSingleton<OidcGrantWithdrawal>();
        builder.AddGroupMembershipProvider<OidcDeveloperMembership>();

        // Constructed rather than resolved, because the CORS policy's predicate below has no service
        // provider of its own. Registered unconditionally: an unused snapshot costs nothing, and it
        // saves OidcApplicationActions from taking an optional dependency.
        var corsOrigins = new OidcCorsOrigins();
        builder.Services.AddSingleton(corsOrigins);

        // Register dynamic CORS policy for OIDC endpoints
        if (options.EnableDynamicCors)
        {
            builder.Services.AddCors(corsOptions =>
            {
                corsOptions.AddPolicy(CorsPolicy, policy =>
                {
                    // Narrowed to the origins registered on enabled OidcApplication documents.
                    // `AllowedCorsOrigins` used to be read by nothing at all, which made the
                    // "validated at runtime" comment that once sat here describe a control that did
                    // not exist.
                    //
                    // Any-origin was defensible for what this policy covers — a public OIDC client
                    // has no secret, so `/token` is *designed* to be called cross-origin and its
                    // control is PKCE, not the Origin header. Narrowing is defence in depth: it
                    // stops a hostile page burning a victim's authorization code.
                    //
                    // ⚠️ Do NOT add AllowCredentials here. Echoing a specific origin makes it
                    // reachable where `AllowAnyOrigin` made it impossible by construction — the
                    // wildcard's safety property was load-bearing, and narrowing quietly removes
                    // it. See docs/guide-cors.md.
                    policy.SetIsOriginAllowed(corsOrigins.IsAllowed)
                          .AllowAnyHeader()
                          .AllowAnyMethod();
                });
            });
        }

        // Register OIDC endpoints
        // The user-generic endpoints close here, at map time, when every Add* call has run - so
        // AddAuthentication<TUser>() may come before or after this method.
        var registry = builder.Registry;
        builder.Registry.AddEndpoints(endpoints =>
        {
            // Checked first: the generated mapping reads SparkAuthenticationOptions (the local-
            // credentials group's IsEnabled), which only AddAuthentication<TUser>() registers.
            var userType = OidcUserEndpoints.RequireUserType(registry.IdentityUserType);
            endpoints.MapSparkIdentityProviderEndpoints();
            OidcUserEndpoints.Map(endpoints, userType);
        });

        // Register middleware to deploy indexes
        builder.Registry.AddMiddleware(app =>
        {
            // ⚠️ No UseCors here. `UseSpark` registers the CORS middleware unconditionally — the same
            // arrangement as antiforgery — so this module declares only what its endpoints need
            // (`RequireCors`) and leaves the pipeline alone.
            //
            // It used to call `app.UseCors("SparkOidcCors")`, which applies a policy to the ENTIRE
            // pipeline: merely enabling the identity provider let any page on any origin read the
            // anonymous view of `/spark/types`, `/spark/translations`, `/spark/permissions/*` and
            // `/spark/auth/capabilities`. A module arranging the pipeline for itself is how a local
            // decision became a global one.

            // The interactive pages must not be framable. Every one of them turns a single click
            // into a security decision — granting a client access, or removing it — and a framed
            // page makes that click something an attacker can arrange. Nothing set this before;
            // the consent screen has been framable since it was written.
            //
            // Middleware, not an endpoint filter (D6): a filter would miss the 404s, 405s and
            // short-circuited responses under the prefix. The prefix is the group's own, so the two
            // cannot drift apart; the three /connect groups share it (Groups.cs).
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments(OidcConnectGroup.Prefix))
                {
                    context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
                    context.Response.Headers["X-Frame-Options"] = "DENY";
                }

                await next();
            });

            var documentStore = app.ApplicationServices.GetRequiredService<IDocumentStore>();
            new OidcApplications_ByClientId().Execute(documentStore);

            // Load the CORS origin snapshot before the first request, not lazily: a browser does
            // not retry a preflight it lost, and this fails closed until the load lands.
            if (options.EnableDynamicCors)
            {
                var corsOrigins = app.ApplicationServices.GetRequiredService<OidcCorsOrigins>();
                corsOrigins.Initialize(
                    documentStore,
                    app.ApplicationServices.GetService<ILoggerFactory>()?.CreateLogger<OidcCorsOrigins>());
                corsOrigins.LoadAsync().GetAwaiter().GetResult();
            }

            new OidcGrants_BySubject().Execute(documentStore);

            // Authorization requests carry @expires, so RavenDB reaps them itself rather than
            // needing a sweeper. Deletion is housekeeping only — an expired request is refused
            // on read regardless of whether the document is still there.
            //
            // The frequency matches MintPlayer.Spark.Messaging, which configures the same
            // database-level setting; they must agree or whichever starts last wins.
            documentStore.Maintenance.Send(new ConfigureExpirationOperation(new ExpirationConfiguration
            {
                Disabled = false,
                DeleteFrequencyInSec = 36 * 60 * 60, // 36 hours (community license minimum)
            }));
        });

        return builder;
    }

    /// <summary>
    /// The policy the OIDC endpoints a browser reaches with <c>fetch</c> opt into, and nothing else.
    /// </summary>
    /// <remarks>
    /// Discovery and JWKS are fetched by every SPA OIDC library on boot; <c>/token</c> is the PKCE
    /// code exchange; <c>/userinfo</c> and <c>/revoke</c> are called with the resulting token.
    /// Everything else under <c>/connect</c> — authorize, login, consent, applications — is reached by
    /// top-level navigation, which is not a CORS request at all, so opting those in would grant
    /// something no caller can use.
    ///
    /// ⚠️ <c>/introspect</c> is deliberately left out: it is a resource-server-to-provider call
    /// authenticated by client credentials, so a browser has no business making it.
    /// </remarks>
    internal const string CorsPolicy = "SparkOidcCors";

}
