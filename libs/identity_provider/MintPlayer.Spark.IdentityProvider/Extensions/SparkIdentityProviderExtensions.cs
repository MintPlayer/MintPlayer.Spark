using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Builder;
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

        // Register dynamic CORS policy for OIDC endpoints
        if (options.EnableDynamicCors)
        {
            builder.Services.AddCors(corsOptions =>
            {
                corsOptions.AddPolicy("SparkOidcCors", policy =>
                {
                    policy.SetIsOriginAllowed(_ => true) // Validated at runtime below
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
            if (options.EnableDynamicCors)
            {
                app.UseCors("SparkOidcCors");
            }

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

    private static IEndpointRouteBuilder MapIdentityProviderEndpoints(this IEndpointRouteBuilder endpoints, SparkIdentityProviderOptions options)
    {
        // Discovery endpoints (well-known paths)
        endpoints.MapGet("/.well-known/openid-configuration", Discovery.Handle);
        endpoints.MapGet("/.well-known/jwks", Jwks.Handle);

        // OIDC protocol endpoints
        var connectGroup = endpoints.MapGroup("/connect");
        connectGroup.MapGet("/authorize", (Delegate)Authorize.Handle);
        connectGroup.MapGet("/login", (Delegate)Login.HandleGet);
        connectGroup.MapPost("/login", (Delegate)Login.HandlePost).RequireAntiforgery();
        connectGroup.MapGet("/consent", (Delegate)Consent.HandleGet);
        connectGroup.MapPost("/consent", (Delegate)Consent.HandlePost).RequireAntiforgery();
        connectGroup.MapGet("/two-factor", (Delegate)TwoFactor.HandleGet);
        connectGroup.MapPost("/two-factor", (Delegate)TwoFactor.HandlePost).RequireAntiforgery();
        connectGroup.MapGet("/applications", (Delegate)ConnectedApplications.HandleGet);
        connectGroup.MapPost("/applications/revoke", (Delegate)ConnectedApplications.HandleRevoke).RequireAntiforgery();

        // Deliberately NOT antiforgery-protected: these are machine endpoints authenticated by
        // client credentials, never by an ambient cookie, so there is no ambient authority for
        // a cross-site request to borrow — and a token that has to be presented cannot be
        // supplied by the browser on the caller's behalf. Requiring a token here would simply
        // break every conforming OAuth client.
        connectGroup.MapPost("/token", (Delegate)Token.Handle);
        connectGroup.MapGet("/userinfo", (Delegate)UserInfo.Handle);
        connectGroup.MapGet("/logout", (Delegate)Logout.Handle);
        connectGroup.MapPost("/introspect", (Delegate)Introspection.Handle);
        connectGroup.MapPost("/revoke", (Delegate)Revocation.Handle);

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
