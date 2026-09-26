using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Endpoints;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Expiration;

namespace MintPlayer.Spark.IdentityProvider.Extensions;

public static class SparkIdentityProviderExtensions
{
    /// <summary>
    /// The application's local-credential mode, or <see cref="SparkLocalCredentials.Full"/> when the
    /// identity provider is used without <c>AddAuthentication</c> — in which case nothing has
    /// expressed an opinion and the provider keeps its own login page.
    /// </summary>
    internal static SparkLocalCredentials LocalCredentialsOf(IServiceProvider services) =>
        (services.GetService(typeof(SparkAuthenticationOptions)) as SparkAuthenticationOptions)
            ?.LocalCredentials ?? SparkLocalCredentials.Full;

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
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        // Register services
        builder.Services.AddSingleton(sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            return new OidcSigningKeyService(env, options.SigningKeyPath);
        });
        builder.Services.AddSingleton<OidcTokenGenerator>();
        builder.Services.AddHostedService<OidcTokenCleanupService>();

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
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkIdentityProviderEndpoints());

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
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/connect"))
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

            new OidcTokens_ByExpiration().Execute(documentStore);
            new OidcAuthorizations_BySubject().Execute(documentStore);

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
