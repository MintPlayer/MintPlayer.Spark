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

            var documentStore = app.ApplicationServices.GetRequiredService<IDocumentStore>();
            new OidcApplications_ByClientId().Execute(documentStore);
            new OidcTokens_ByReferenceId().Execute(documentStore);
            new OidcTokens_ByExpiration().Execute(documentStore);
            new OidcAuthorizations_BySubjectAndApplication().Execute(documentStore);
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
        connectGroup.MapPost("/login", (Delegate)Login.HandlePost);
        connectGroup.MapGet("/consent", (Delegate)Consent.HandleGet);
        connectGroup.MapPost("/consent", (Delegate)Consent.HandlePost);
        connectGroup.MapGet("/two-factor", (Delegate)TwoFactor.HandleGet);
        connectGroup.MapPost("/two-factor", (Delegate)TwoFactor.HandlePost);
        connectGroup.MapPost("/token", (Delegate)Token.Handle);
        connectGroup.MapGet("/userinfo", (Delegate)UserInfo.Handle);
        connectGroup.MapGet("/logout", (Delegate)Logout.Handle);
        connectGroup.MapPost("/introspect", (Delegate)Introspection.Handle);
        connectGroup.MapPost("/revoke", (Delegate)Revocation.Handle);

        return endpoints;
    }
}
