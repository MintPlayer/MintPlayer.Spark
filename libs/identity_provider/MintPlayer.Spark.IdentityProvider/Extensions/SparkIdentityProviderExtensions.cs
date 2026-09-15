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
    private static SparkLocalCredentials LocalCredentialsOf(IServiceProvider services) =>
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
        builder.Registry.AddEndpoints(endpoints => endpoints.MapIdentityProviderEndpoints(options));

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
    private const string CorsPolicy = "SparkOidcCors";

    /// <summary>
    /// Opts one endpoint into <see cref="CorsPolicy"/> — but only when the application enabled it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>The condition is not tidiness; without it the endpoint throws on every request.</b> The
    /// named policy is registered only when the option is on, and the CORS middleware throws when an
    /// endpoint asks for a policy that does not exist. Since <c>EnableDynamicCors</c> is off by
    /// default, applying <c>RequireCors</c> unconditionally would make <c>/connect/token</c> — the
    /// PKCE code exchange, the endpoint the whole provider exists to serve — fail in the default
    /// configuration. Caught by
    /// <c>OidcCorsScopeTests.By_default_the_identity_provider_grants_no_cross_origin_access</c>.
    /// </para>
    /// <para>
    /// ⚠️ It first threw for a <i>different</i> reason — <i>"contains CORS metadata, but a middleware
    /// was not found that supports CORS"</i> — because this module registered the CORS middleware only
    /// when its own flag was set. <c>UseSpark</c> now registers it unconditionally, the way it does
    /// antiforgery, so that failure mode is gone for every module. This condition guards the one that
    /// remains, which is about the policy rather than the pipeline.
    /// </para>
    /// </remarks>
    private static TBuilder WithOidcCors<TBuilder>(
        this TBuilder builder, SparkIdentityProviderOptions options)
        where TBuilder : IEndpointConventionBuilder
        => options.EnableDynamicCors ? builder.RequireCors(CorsPolicy) : builder;

    private static IEndpointRouteBuilder MapIdentityProviderEndpoints(this IEndpointRouteBuilder endpoints, SparkIdentityProviderOptions options)
    {
        // Discovery endpoints (well-known paths)
        endpoints.MapGet("/.well-known/openid-configuration", Discovery.Handle).WithOidcCors(options);
        endpoints.MapGet("/.well-known/jwks", Jwks.Handle).WithOidcCors(options);

        // OIDC protocol endpoints
        var connectGroup = endpoints.MapGroup("/connect");
        connectGroup.MapGet("/authorize", (Delegate)Authorize.Handle);

        // The provider's own password form. It honours the application's SparkLocalCredentials mode
        // for the same reason /spark/auth/login does — and because it would otherwise be a way to
        // keep a password surface alive in an application that had turned local credentials off.
        // The protocol endpoints below are untouched: an identity provider that federates to an
        // upstream provider still needs every one of them.
        if (LocalCredentialsOf(endpoints.ServiceProvider) != SparkLocalCredentials.Disabled)
        {
            connectGroup.MapGet("/login", (Delegate)Login.HandleGet);
            connectGroup.MapPost("/login", (Delegate)Login.HandlePost).RequireAntiforgery();
            connectGroup.MapGet("/two-factor", (Delegate)TwoFactor.HandleGet);
            connectGroup.MapPost("/two-factor", (Delegate)TwoFactor.HandlePost).RequireAntiforgery();
        }

        connectGroup.MapGet("/consent", (Delegate)Consent.HandleGet);
        connectGroup.MapPost("/consent", (Delegate)Consent.HandlePost).RequireAntiforgery();
        connectGroup.MapGet("/applications", (Delegate)ConnectedApplications.HandleGet);
        connectGroup.MapPost("/applications/revoke", (Delegate)ConnectedApplications.HandleRevoke).RequireAntiforgery();

        // Deliberately NOT antiforgery-protected: these are machine endpoints authenticated by
        // client credentials, never by an ambient cookie, so there is no ambient authority for
        // a cross-site request to borrow — and a token that has to be presented cannot be
        // supplied by the browser on the caller's behalf. Requiring a token here would simply
        // break every conforming OAuth client.
        connectGroup.MapPost("/token", (Delegate)Token.Handle).WithOidcCors(options);
        connectGroup.MapGet("/userinfo", (Delegate)UserInfo.Handle).WithOidcCors(options);
        connectGroup.MapGet("/logout", (Delegate)Logout.Handle);
        connectGroup.MapPost("/introspect", (Delegate)Introspection.Handle);
        connectGroup.MapPost("/revoke", (Delegate)Revocation.Handle).WithOidcCors(options);

        return endpoints;
    }

    /// <summary>
    /// Marks a route for antiforgery validation. Spark's middleware validates any endpoint
    /// carrying <c>IAntiforgeryMetadata</c> (<c>SparkMiddleware</c>); these handlers read the
    /// body with <c>ReadFormAsync</c> rather than <c>[FromForm]</c>, so minimal APIs never
    /// inferred the metadata for them and the pages went unprotected.
    /// </summary>
    private static RouteHandlerBuilder RequireAntiforgery(this RouteHandlerBuilder builder)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
}
